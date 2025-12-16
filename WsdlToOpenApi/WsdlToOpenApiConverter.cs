using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web.Services.Description;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;
using Microsoft.OpenApi;


namespace WsdlToOpenApi;

/// <summary>
/// Converter enhanced to discover and load WSDL imports and XSD includes/imports recursively.
/// - Recursively fetches imported WSDLs and merges their messages, portTypes, bindings, services and schemas.
/// - Recursively resolves XSD include/import/schemaLocation URLs found in schemas and adds them to the XmlSchemaSet.
/// - For each operation: the output action's returned type is discovered and used as the OpenAPI response
///   schema for the corresponding input action. If the returned type is a simple type or a sequence (array)
///   of simple types, that simple/array schema is used directly; otherwise a component $ref is generated.
/// - Honors an operation's documentation element "POST" to force a POST OpenAPI operation.
/// - Input parameters that are sequences (repeated elements) are represented as arrays in the generated OpenAPI.
/// - If a complex element is only a wrapper that contains a single sequence child, the wrapper is ignored and the
///   inner sequence is transformed into an array in the OpenAPI schema (outer wrapper is not generated as a component).
/// - Supports file:// URLs for local WSDL/XSD files.
/// - Uses Microsoft.OpenApi (OpenAPI.NET) to construct and serialize the OpenAPI document.
/// </summary>
public class WsdlToOpenApiConverter
{
    private static readonly HttpClient HttpClient = new();

    public async Task<string> GenerateOpenApiFromWsdlAsync(string wsdlUrl)
    {
        var (sd, schemasByBase) = await LoadAndMergeServiceDescriptionsAsync(wsdlUrl);

        var schemaSet = new XmlSchemaSet();
        var visitedSchemaUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add initial schemas grouped by baseUri and resolve their includes/imports recursively
        foreach (var kvp in schemasByBase)
        {
            var baseUri = kvp.Key;
            foreach (var schema in kvp.Value)
            {
                await AddSchemaAndResolveIncludesAsync(schema, baseUri, schemaSet, visitedSchemaUris);
            }
        }

        schemaSet.Compile();

        // Create OpenAPI document using Microsoft.OpenApi models
        var doc = new OpenApiDocument
        {
            Info = new OpenApiInfo
            {
                Title = sd.Name ?? "wsdl-conversion",
                Version = "1.0.0"
            },
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() }
        };

        // Map of processed complex types to component names
        var processedComplex = new Dictionary<XmlQualifiedName, string>();

        foreach (PortType portType in sd.PortTypes)
        {
            foreach (Operation op in portType.Operations)
            {
                var pathKey = $"/{Sanitize(portType.Name)}/{Sanitize(op.Name)}";
                var pathItem = new OpenApiPathItem();
                var method = "get";

                var inputMessage = op.Messages.Input?.Message;
                var outputMessage = op.Messages.Output?.Message;

                var operation = new OpenApiOperation
                {
                    Summary = op.Documentation,
                    Parameters = new List<OpenApiParameter>().OfType<IOpenApiParameter>().ToList(),
                    Responses = new OpenApiResponses()
                };

                OpenApiRequestBody? requestBody = null;
                bool requiresBody = false;

                if (inputMessage != null)
                {
                    var wsdlMsg = sd.Messages.Cast<Message>().FirstOrDefault(m => m.Name == inputMessage.Name);
                    if (wsdlMsg != null)
                    {
                        foreach (MessagePart part in wsdlMsg.Parts)
                        {
                            XmlSchemaElement? element = null;
                            XmlSchemaType? resolvedType = null;

                            if (!part.Element.IsEmpty)
                            {
                                element = FindGlobalElement(schemaSet, part.Element);
                                if (element != null)
                                {
                                    resolvedType = ResolveElementType(element, schemaSet);
                                }
                            }
                            else if (!part.Type.IsEmpty)
                            {
                                resolvedType = ResolveXmlSchemaType(part.Type, schemaSet);
                            }

                            // If element is present and repeated -> parameter is an array of item schema
                            if (element != null && element.MaxOccurs > 1m)
                            {
                                var itemSchema = CreateOpenApiSchemaForType(resolvedType, schemaSet, doc.Components, processedComplex);
                                var arraySchema = new OpenApiSchema { Type = JsonSchemaType.Array, Items = itemSchema };
                                operation.Parameters.Add(CreateOpenApiQueryParameter(part.Name ?? element.Name, arraySchema));
                                continue;
                            }

                            var schemaForPart = CreateOpenApiSchemaForType(resolvedType, schemaSet, doc.Components, processedComplex);

                            if (IsSimpleType(resolvedType))
                            {
                                operation.Parameters.Add(CreateOpenApiQueryParameter(part.Name ?? element?.Name ?? part.Name, schemaForPart));
                            }
                            else
                            {
                                var depth = ComputeDepth(resolvedType, schemaSet, 0);
                                if (depth <= 1)
                                {
                                    var fields = GetChildFields(resolvedType, schemaSet);
                                    foreach (var f in fields)
                                    {
                                        operation.Parameters.Add(CreateOpenApiQueryParameter(f.name, MapSimpleTypeToOpenApiSchema(f.type)));
                                    }
                                }
                                else
                                {
                                    requiresBody = true;
                                    if (schemaForPart != null)
                                    {
                                        requestBody = CreateOpenApiRequestBodyFromSchema(schemaForPart);
                                    }
                                }
                            }
                        }

                        if (requiresBody)
                        {
                            method = "post";
                            requestBody ??= CreateDefaultOpenApiRequestBody();
                        }
                    }
                }

                if (string.Equals(op.Documentation?.Trim(), "POST", StringComparison.OrdinalIgnoreCase))
                {
                    method = "post";
                    requestBody ??= CreateDefaultOpenApiRequestBody();
                }

                // Output handling
                if (outputMessage != null)
                {
                    var wsdlOut = sd.Messages.Cast<Message>().FirstOrDefault(m => m.Name == outputMessage.Name);
                    if (wsdlOut != null && wsdlOut.Parts.Count > 0)
                    {
                        var outPart = wsdlOut.Parts[0];
                        var responseSchema = BuildOpenApiSchemaForMessagePart(outPart, schemaSet, doc.Components, processedComplex);
                        operation.Responses["200"] = new OpenApiResponse
                        {
                            Description = "Successful response",
                            Content = new Dictionary<string, IOpenApiMediaType>
                            {
                                { "application/json", new OpenApiMediaType { Schema = responseSchema } }
                            }
                        };
                    }
                    else
                    {
                        operation.Responses["200"] = new OpenApiResponse { Description = "No content" };
                    }
                }
                else
                {
                    operation.Responses["200"] = new OpenApiResponse { Description = "No output message" };
                }

                if (requestBody != null)
                {
                    operation.RequestBody = requestBody;
                }

                // assign operation to path item using the selected method
                // Ensure Operations dictionary is initialized (key type used by earlier API is HttpMethod)
                if (pathItem.Operations == null)
                {
                    pathItem.Operations = new Dictionary<System.Net.Http.HttpMethod, OpenApiOperation>();
                }

                switch (method.ToLowerInvariant())
                {
                    case "get":
                        pathItem.AddOperation(HttpMethod.Get, operation);
                        break;
                    case "post":
                        pathItem.AddOperation(HttpMethod.Post, operation);
                        break;
                    default:
                        pathItem.AddOperation(HttpMethod.Get, operation);
                        break;
                }

                doc.Paths[pathKey] = pathItem;
            }
        }

        // Serialize using OpenAPI.NET
        using var sw = new StringWriter();
        var writer = new OpenApiJsonWriter(sw);
        doc.SerializeAsV3(writer);
        await writer.FlushAsync();
        return sw.ToString();
    }

    // --- OpenAPI.NET helpers (create schemas, parameters, request bodies) ---

    private static OpenApiRequestBody CreateDefaultOpenApiRequestBody()
    {
        return new OpenApiRequestBody
        {
            Content = new Dictionary<string, IOpenApiMediaType>
            {
                { "application/json", new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } } }
            }
        };
    }

    private static OpenApiRequestBody CreateOpenApiRequestBodyFromSchema(IOpenApiSchema schema)
    {
        return new OpenApiRequestBody
        {
            Content = new Dictionary<string, IOpenApiMediaType>
            {
                { "application/json", new OpenApiMediaType { Schema = schema } }
            }
        };
    }

    private static OpenApiParameter CreateOpenApiQueryParameter(string name, IOpenApiSchema schema)
    {
        return new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Schema = schema
        };
    }

    // Build OpenApi schema for a MessagePart (delegates to CreateOpenApiSchemaForType)
    private static IOpenApiSchema BuildOpenApiSchemaForMessagePart(MessagePart part, XmlSchemaSet set, OpenApiComponents components, Dictionary<XmlQualifiedName, string> processed)
    {
        try
        {
            if (!part.Element.IsEmpty)
            {
                var element = FindGlobalElement(set, part.Element);
                if (element != null)
                {
                    var elementType = ResolveElementType(element, set);
                    var baseSchema = CreateOpenApiSchemaForType(elementType, set, components, processed);
                    if (element.MaxOccurs > 1m)
                    {
                        return new OpenApiSchema { Type = JsonSchemaType.Array, Items = baseSchema };
                    }
                    return baseSchema;
                }
            }
            else if (!part.Type.IsEmpty)
            {
                var xmlType = ResolveXmlSchemaType(part.Type, set);
                if (xmlType != null)
                {
                    return CreateOpenApiSchemaForType(xmlType, set, components, processed);
                }
            }
        }
        catch
        {
            // fall through
        }

        return new OpenApiSchema { Type = JsonSchemaType.String };
    }

    // Create an OpenApiSchema for an XmlSchemaType. For complex types it registers a component schema and returns a $ref schema.
    private static IOpenApiSchema CreateOpenApiSchemaForType(XmlSchemaType? type, XmlSchemaSet set, OpenApiComponents components, Dictionary<XmlQualifiedName, string> processed)
    {
        if (type == null)
        {
            return new OpenApiSchema { Type = JsonSchemaType.String };
        }

        if (IsSimpleType(type))
        {
            return MapSimpleTypeToOpenApiSchema(type);
        }

        if (type is XmlSchemaComplexType ct)
        {
            // wrapper pattern: single sequence child -> array of inner type
            var inner = GetSingleSequenceChild(ct);
            if (inner != null)
            {
                var innerType = inner.ElementSchemaType ?? ResolveXmlSchemaType(inner.SchemaTypeName, set);
                if (innerType != null)
                {
                    var itemSchema = CreateOpenApiSchemaForType(innerType, set, components, processed);
                    return new OpenApiSchema { Type = JsonSchemaType.Array, Items = itemSchema };
                }
            }

            // otherwise create/ensure a component schema and return a reference schema
            var compName = EnsureComponentForTypeOpenApi(type, set, components, processed);
            return new OpenApiSchemaReference(compName);
        }

        // fallback
        return new OpenApiSchema { Type = JsonSchemaType.String };
    }

    // Ensure a component schema exists for a complex XmlSchemaType. Returns the component name.
    // Registers the generated OpenApiSchema into components.Schemas.
    private static string EnsureComponentForTypeOpenApi(XmlSchemaType type, XmlSchemaSet set, OpenApiComponents components, Dictionary<XmlQualifiedName, string> processed)
    {
        var qn = type.QualifiedName;
        var compName = !qn.IsEmpty ? qn.Name : $"AnonType_{processed.Count + 1}";

        if (processed.TryGetValue(qn, out var existing)) return existing;

        processed[qn] = compName;

        var schema = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = new Dictionary<string, IOpenApiSchema>() };

        // Ensure components.Schemas dictionary is initialized before adding entries
        if (components.Schemas == null)
        {
            components.Schemas = new Dictionary<string, IOpenApiSchema>();
        }

        // handle complex content extension -> include base refs and extension properties
        if (type is XmlSchemaComplexType ct)
        {
            if (ct.ContentModel is XmlSchemaComplexContent complexContent && complexContent.Content is XmlSchemaComplexContentExtension extension)
            {
                if (!extension.BaseTypeName.IsEmpty)
                {
                    var baseType = ResolveXmlSchemaType(extension.BaseTypeName, set);
                    if (baseType != null && !IsSimpleType(baseType))
                    {
                        var baseName = EnsureComponentForTypeOpenApi(baseType, set, components, processed);
                        // include by adding an extension property that is a $ref to base (keeps behavior similar to previous implementation)
                        schema.Properties[$"_extends_{extension.BaseTypeName.Name}"] = new OpenApiSchemaReference(baseName);
                    }
                }

                if (extension.Particle is XmlSchemaSequence extSeq)
                {
                    foreach (XmlSchemaObject item in extSeq.Items)
                    {
                        if (item is XmlSchemaElement child)
                        {
                            AddPropertyForChildOpenApi(child, set, schema.Properties, components, processed);
                        }
                    }
                }
            }
            else if (ct.Particle is XmlSchemaSequence seq)
            {
                foreach (XmlSchemaObject item in seq.Items)
                {
                    if (item is XmlSchemaElement child)
                    {
                        AddPropertyForChildOpenApi(child, set, schema.Properties, components, processed);
                    }
                }
            }
            else if (ct.Particle is XmlSchemaChoice choice)
            {
                foreach (XmlSchemaObject item in choice.Items)
                {
                    if (item is XmlSchemaElement child)
                    {
                        AddPropertyForChildOpenApi(child, set, schema.Properties, components, processed);
                    }
                }
            }
        }

        components.Schemas[compName] = schema;

        return compName;
    }

    private static void AddPropertyForChildOpenApi(XmlSchemaElement child, XmlSchemaSet set, IDictionary<string, IOpenApiSchema> props, OpenApiComponents components, Dictionary<XmlQualifiedName, string> processed)
    {
        var childType = child.ElementSchemaType ?? ResolveXmlSchemaType(child.SchemaTypeName, set) ?? child.ElementSchemaType!;
        if (IsSimpleType(childType))
        {
            props[child.Name ?? child.QualifiedName.Name] = MapSimpleTypeToOpenApiSchema(childType);
        }
        else
        {
            var nestedName = EnsureComponentForTypeOpenApi(childType, set, components, processed);
            props[child.Name ?? child.QualifiedName.Name] = new OpenApiSchemaReference(nestedName);
        }
    }

    private static OpenApiSchema MapSimpleTypeToOpenApiSchema(XmlSchemaType? type)
    {
        var res = new OpenApiSchema { Type = JsonSchemaType.String };

        if (type == null) return res;

        var qn = type.QualifiedName;
        if (qn.Namespace == XmlSchema.Namespace)
        {
            switch (qn.Name)
            {
                case "string":
                case "normalizedString":
                    res.Type = JsonSchemaType.String;
                    break;
                case "boolean":
                    res.Type = JsonSchemaType.Boolean;
                    break;
                case "int":
                case "integer":
                case "short":
                case "byte":
                    res.Type = JsonSchemaType.Integer;
                    res.Format = "int32";
                    break;
                case "long":
                    res.Type = JsonSchemaType.Integer;
                    res.Format = "int64";
                    break;
                case "decimal":
                case "double":
                case "float":
                    res.Type = JsonSchemaType.Number;
                    break;
                case "dateTime":
                    res.Type = JsonSchemaType.String;
                    res.Format = "date-time";
                    break;
                case "date":
                    res.Type = JsonSchemaType.String;
                    res.Format = "date";
                    break;
                case "base64Binary":
                    res.Type = JsonSchemaType.String;
                    res.Format = "byte";
                    break;
                default:
                    res.Type = JsonSchemaType.String;
                    break;
            }
        }

        return res;
    }

    // --- existing WSDL/XSD helpers (unchanged behavior) ---

    private async Task<(ServiceDescription merged, Dictionary<Uri, List<XmlSchema>> schemasByBase)> LoadAndMergeServiceDescriptionsAsync(string wsdlUrl)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new List<(ServiceDescription sd, Uri baseUri, string xml)>();

        async Task LoadRecursiveAsync(string url)
        {
            var uri = new Uri(url);
            var absolute = uri.AbsoluteUri;
            if (!visited.Add(absolute)) return;

            var xml = await ReadTextFromUriAsync(uri);

            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr);
            var sd = ServiceDescription.Read(xr, true);
            var baseUri = uri;
            descriptions.Add((sd, baseUri, xml));

            var doc = XDocument.Parse(xml);
            XNamespace wsdlNs = "http://schemas.xmlsoap.org/wsdl/";
            foreach (var imp in doc.Descendants(wsdlNs + "import"))
            {
                var loc = (string?)imp.Attribute("location");
                if (!string.IsNullOrWhiteSpace(loc))
                {
                    var resolved = new Uri(baseUri, loc).AbsoluteUri;
                    await LoadRecursiveAsync(resolved);
                }
            }
        }

        await LoadRecursiveAsync(wsdlUrl);

        var merged = descriptions[0].sd;

        var schemasByBase = new Dictionary<Uri, List<XmlSchema>>();
        var schemaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (XmlSchema s in descriptions[0].sd.Types.Schemas)
        {
            var key = !string.IsNullOrWhiteSpace(s.SourceUri) ? s.SourceUri : $"{descriptions[0].baseUri.AbsoluteUri}#{s.TargetNamespace}";
            if (schemaKeys.Add(key))
            {
                if (!schemasByBase.TryGetValue(descriptions[0].baseUri, out var list))
                {
                    list = new List<XmlSchema>();
                    schemasByBase[descriptions[0].baseUri] = list;
                }
                list.Add(s);
            }
        }

        for (int i = 1; i < descriptions.Count; i++)
        {
            var sd = descriptions[i].sd;
            var baseUri = descriptions[i].baseUri;

            foreach (Message m in sd.Messages)
            {
                if (!merged.Messages.Cast<Message>().Any(x => x.Name == m.Name))
                    merged.Messages.Add(m);
            }

            foreach (PortType p in sd.PortTypes)
            {
                if (!merged.PortTypes.Cast<PortType>().Any(x => x.Name == p.Name))
                    merged.PortTypes.Add(p);
            }

            foreach (Binding b in sd.Bindings)
            {
                if (!merged.Bindings.Cast<Binding>().Any(x => x.Name == b.Name))
                    merged.Bindings.Add(b);
            }

            foreach (Service s in sd.Services)
            {
                if (!merged.Services.Cast<Service>().Any(x => x.Name == s.Name))
                    merged.Services.Add(s);
            }

            foreach (XmlSchema s in sd.Types.Schemas)
            {
                var key = !string.IsNullOrWhiteSpace(s.SourceUri) ? s.SourceUri : $"{baseUri.AbsoluteUri}#{s.TargetNamespace}";
                if (schemaKeys.Add(key))
                {
                    if (!schemasByBase.TryGetValue(baseUri, out var list))
                    {
                        list = new List<XmlSchema>();
                        schemasByBase[baseUri] = list;
                    }
                    list.Add(s);
                    merged.Types.Schemas.Add(s);
                }
            }
        }

        return (merged, schemasByBase);
    }

    private static async Task<string> ReadTextFromUriAsync(Uri uri)
    {
        if (uri.IsFile)
        {
            return await File.ReadAllTextAsync(uri.LocalPath);
        }
        return await HttpClient.GetStringAsync(uri);
    }

    private async Task AddSchemaAndResolveIncludesAsync(XmlSchema schema, Uri baseUri, XmlSchemaSet set, HashSet<string> visitedSchemaUris)
    {
        var schemaKey = !string.IsNullOrWhiteSpace(schema.SourceUri) ? schema.SourceUri : $"{baseUri.AbsoluteUri}#{schema.TargetNamespace}";
        if (!visitedSchemaUris.Add(schemaKey))
        {
            if (!set.Schemas().Cast<XmlSchema>().Any(s => s.SourceUri == schema.SourceUri))
            {
                set.Add(schema);
            }
            return;
        }

        set.Add(schema);

        foreach (var include in schema.Includes.Cast<XmlSchemaExternal>())
        {
            if (string.IsNullOrWhiteSpace(include.SchemaLocation)) continue;
            try
            {
                var resolved = new Uri(baseUri, include.SchemaLocation);
                var xml = await ReadTextFromUriAsync(resolved);
                using var sr = new StringReader(xml);
                using var xr = XmlReader.Create(sr, new XmlReaderSettings(), resolved.AbsoluteUri);
                var childSchema = XmlSchema.Read(xr, null);
                if (childSchema != null)
                {
                    await AddSchemaAndResolveIncludesAsync(childSchema, resolved, set, visitedSchemaUris);
                }
            }
            catch
            {
                // ignore individual include failures — continue processing other includes
            }
        }
    }

    private static OpenApiResponse CreateSimpleResponse(OpenApiSchema schema)
    {
        return new OpenApiResponse
        {
            Description = "Successful response",
            Content = new Dictionary<string, IOpenApiMediaType>
            {
                { "application/json", new OpenApiMediaType { Schema = schema } }
            }
        };
    }

    private static XmlSchemaElement? FindGlobalElement(XmlSchemaSet set, XmlQualifiedName qname)
    {
        foreach (XmlSchema schema in set.Schemas())
        {
            if (!qname.IsEmpty && schema.Elements.Contains(qname))
            {
                return (XmlSchemaElement)schema.Elements[qname];
            }
        }
        return null;
    }

    private static XmlSchemaType? ResolveElementType(XmlSchemaElement element, XmlSchemaSet set)
    {
        if (element.ElementSchemaType != null)
            return element.ElementSchemaType;
        if (!element.SchemaTypeName.IsEmpty)
        {
            return ResolveXmlSchemaType(element.SchemaTypeName, set);
        }
        return null;
    }

    private static XmlSchemaType? ResolveXmlSchemaType(XmlQualifiedName qname, XmlSchemaSet set)
    {
        foreach (XmlSchema schema in set.Schemas())
        {
            if (!qname.IsEmpty && schema.SchemaTypes.Contains(qname))
            {
                return (XmlSchemaType)schema.SchemaTypes[qname];
            }
        }

        try
        {
            var builtin = XmlSchemaType.GetBuiltInSimpleType(qname);
            return builtin;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSimpleType(XmlSchemaType? type)
    {
        if (type == null) return true;
        return type is XmlSchemaSimpleType || (type.QualifiedName.Namespace == XmlSchema.Namespace && XmlSchemaType.GetBuiltInSimpleType(type.QualifiedName) != null);
    }

    private static int ComputeDepth(XmlSchemaType? type, XmlSchemaSet set, int current)
    {
        if (type == null) return current;
        if (IsSimpleType(type)) return current;
        int max = current;
        if (type is XmlSchemaComplexType ct)
        {
            if (ct.Particle is XmlSchemaSequence seq)
            {
                foreach (XmlSchemaObject item in seq.Items)
                {
                    if (item is XmlSchemaElement child)
                    {
                        XmlSchemaType childType = child.ElementSchemaType ?? ResolveXmlSchemaType(child.SchemaTypeName, set) ?? child.ElementSchemaType!;
                        max = Math.Max(max, ComputeDepth(childType, set, current + 1));
                    }
                }
            }
            else if (ct.ContentModel is XmlSchemaComplexContent complexContent && complexContent.Content is XmlSchemaComplexContentExtension extension && extension.Particle is XmlSchemaSequence extSeq)
            {
                foreach (XmlSchemaObject item in extSeq.Items)
                {
                    if (item is XmlSchemaElement child)
                    {
                        XmlSchemaType childType = child.ElementSchemaType ?? ResolveXmlSchemaType(child.SchemaTypeName, set) ?? child.ElementSchemaType!;
                        max = Math.Max(max, ComputeDepth(childType, set, current + 1));
                    }
                }
            }
        }
        return max;
    }

    private static List<(string name, XmlSchemaType type)> GetChildFields(XmlSchemaType? type, XmlSchemaSet set)
    {
        var list = new List<(string, XmlSchemaType)>();
        if (type is XmlSchemaComplexType ct)
        {
            if (ct.Particle is XmlSchemaSequence seq)
            {
                foreach (XmlSchemaObject item in seq.Items)
                {
                    if (item is XmlSchemaElement child)
                    {
                        var childType = child.ElementSchemaType ?? ResolveXmlSchemaType(child.SchemaTypeName, set) ?? child.ElementSchemaType!;
                        list.Add((child.Name ?? child.QualifiedName.Name, childType));
                    }
                }
            }
            else if (ct.ContentModel is XmlSchemaComplexContent complexContent && complexContent.Content is XmlSchemaComplexContentExtension extension)
            {
                if (extension.Particle is XmlSchemaSequence extSeq)
                {
                    foreach (XmlSchemaObject item in extSeq.Items)
                    {
                        if (item is XmlSchemaElement child)
                        {
                            var childType = child.ElementSchemaType ?? ResolveXmlSchemaType(child.SchemaTypeName, set) ?? child.ElementSchemaType!;
                            list.Add((child.Name ?? child.QualifiedName.Name, childType));
                        }
                    }
                }
            }
        }
        return list;
    }

    private static XmlSchemaElement? GetSingleSequenceChild(XmlSchemaComplexType ct)
    {
        if (ct == null) return null;

        if (ct.Particle is XmlSchemaSequence seq && seq.Items.Count == 1 && seq.Items[0] is XmlSchemaElement child)
        {
            return child;
        }

        if (ct.ContentModel is XmlSchemaComplexContent complexContent && complexContent.Content is XmlSchemaComplexContentExtension extension)
        {
            if (extension.Particle is XmlSchemaSequence extSeq && extSeq.Items.Count == 1 && extSeq.Items[0] is XmlSchemaElement extChild)
            {
                return extChild;
            }
        }

        return null;
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        return new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
    }
}