using System.Text.Json;
using System.Web.Services.Description;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;

namespace WsdlToOpenApi
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: WsdlToOpenApi <wsdl-url> [output.json]");
                return;
            }

            var wsdlUrl = args[0];
            var outputFile = args.Length > 1 ? args[1] : "wsdl-openapi.json";

            try
            {
                var converter = new WsdlToOpenApiConverter();
                var openApiJson = await converter.GenerateOpenApiFromWsdlAsync(wsdlUrl);
                await File.WriteAllTextAsync(outputFile, openApiJson);
                Console.WriteLine($"OpenAPI saved to: {outputFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

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
    /// - Centralized schema/type handling helpers to remove duplicated logic.
    /// </summary>
    internal class WsdlToOpenApiConverter
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

            var doc = new Dictionary<string, object?>
            {
                ["openapi"] = "3.0.3",
                ["info"] = new Dictionary<string, object?>
                {
                    ["title"] = sd.Name ?? "wsdl-conversion",
                    ["version"] = "1.0.0"
                },
                ["paths"] = new Dictionary<string, object>(),
                ["components"] = new Dictionary<string, object?>
                {
                    ["schemas"] = new Dictionary<string, object?>()
                }
            };

            var components = (Dictionary<string, object?>)doc["components"]!;
            var schemas = (Dictionary<string, object?>)components["schemas"]!;
            var processedComplex = new Dictionary<XmlQualifiedName, string>();

            foreach (PortType portType in sd.PortTypes)
            {
                foreach (Operation op in portType.Operations)
                {
                    var path = $"/{Sanitize(portType.Name)}/{Sanitize(op.Name)}";
                    var pathItem = new Dictionary<string, object?>();
                    var method = "get";

                    var inputMessage = op.Messages.Input?.Message;
                    var outputMessage = op.Messages.Output?.Message;

                    var parameters = new List<Dictionary<string, object?>>();
                    Dictionary<string, object?>? requestBody = null;
                    Dictionary<string, object?> responses = new Dictionary<string, object?>();

                    if (inputMessage != null)
                    {
                        var wsdlMsg = sd.Messages.Cast<Message>().FirstOrDefault(m => m.Name == inputMessage.Name);
                        if (wsdlMsg != null)
                        {
                            bool requiresBody = false;

                            foreach (MessagePart part in wsdlMsg.Parts)
                            {
                                // unify resolution of element/type
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
                                    var itemSchema = CreateSchemaForType(resolvedType, schemaSet, schemas, processedComplex);
                                    var arraySchema = CreateArraySchema(itemSchema);
                                    parameters.Add(CreateQueryParameter(part.Name ?? element.Name, arraySchema));
                                    continue;
                                }

                                // If resolvedType is a wrapper around a single sequence child, CreateSchemaForType will return an array schema
                                var schemaForPart = CreateSchemaForType(resolvedType, schemaSet, schemas, processedComplex);

                                // Decide whether we can use query params (simple or flattened shallow) or should use requestBody
                                if (IsSimpleType(resolvedType))
                                {
                                    parameters.Add(CreateQueryParameter(part.Name ?? element?.Name ?? part.Name, schemaForPart));
                                }
                                else
                                {
                                    var depth = ComputeDepth(resolvedType, schemaSet, 0);
                                    if (depth <= 1)
                                    {
                                        // flatten child elements as query parameters
                                        var fields = GetChildFields(resolvedType, schemaSet);
                                        foreach (var f in fields)
                                        {
                                            parameters.Add(CreateQueryParameter($"{f.name}".TrimStart('.'), MapSimpleTypeToOpenApi(f.type)));
                                        }
                                    }
                                    else
                                    {
                                        requiresBody = true;
                                        // if schemaForPart is an array or $ref or object schema, use as requestBody
                                        if (schemaForPart != null)
                                        {
                                            requestBody = CreateRequestBodyFromSchema(schemaForPart);
                                        }
                                    }
                                }
                            }

                            if (requiresBody)
                            {
                                method = "post";
                                requestBody ??= DefaultRequestBody();
                            }
                        }
                    }

                    if (string.Equals(op.Documentation?.Trim(), "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        method = "post";
                        requestBody ??= DefaultRequestBody();
                    }

                    if (outputMessage != null)
                    {
                        var wsdlOut = sd.Messages.Cast<Message>().FirstOrDefault(m => m.Name == outputMessage.Name);
                        if (wsdlOut != null && wsdlOut.Parts.Count > 0)
                        {
                            var outPart = wsdlOut.Parts[0];
                            var responseSchema = BuildSchemaForMessagePart(outPart, schemaSet, schemas, processedComplex);
                            responses["200"] = CreateSimpleResponse(responseSchema);
                        }
                        else
                        {
                            responses["200"] = new Dictionary<string, object?> { ["description"] = "No content" };
                        }
                    }
                    else
                    {
                        responses["200"] = new Dictionary<string, object?> { ["description"] = "No output message" };
                    }

                    var operationObj = new Dictionary<string, object?>
                    {
                        ["summary"] = op.Documentation,
                        ["parameters"] = parameters,
                        ["responses"] = responses
                    };

                    if (requestBody != null)
                    {
                        operationObj["requestBody"] = requestBody;
                    }

                    pathItem[method] = operationObj;
                    ((Dictionary<string, object>)doc["paths"]!)[path] = pathItem;
                }
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(doc, options);
        }

        // Centralized helpers

        private static Dictionary<string, object?> DefaultRequestBody()
        {
            return new Dictionary<string, object?>
            {
                ["content"] = new Dictionary<string, object?>
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = new Dictionary<string, object?> { ["type"] = "object" }
                    }
                }
            };
        }

        private static Dictionary<string, object?> CreateRequestBodyFromSchema(Dictionary<string, object?> schema)
        {
            return new Dictionary<string, object?>
            {
                ["content"] = new Dictionary<string, object?>
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = schema
                    }
                }
            };
        }

        private static Dictionary<string, object?> CreateArraySchema(Dictionary<string, object?> itemSchema)
        {
            return new Dictionary<string, object?> { ["type"] = "array", ["items"] = itemSchema };
        }

        // Build schema for a MessagePart's returned type using centralized logic
        private static Dictionary<string, object?> BuildSchemaForMessagePart(MessagePart part, XmlSchemaSet set, Dictionary<string, object?> schemas, Dictionary<XmlQualifiedName, string> processed)
        {
            try
            {
                if (!part.Element.IsEmpty)
                {
                    var element = FindGlobalElement(set, part.Element);
                    if (element != null)
                    {
                        // If element repeats -> array of element type
                        var elementType = ResolveElementType(element, set);
                        var baseSchema = CreateSchemaForType(elementType, set, schemas, processed);
                        if (element.MaxOccurs > 1m)
                        {
                            return CreateArraySchema(baseSchema);
                        }
                        return baseSchema;
                    }
                }
                else if (!part.Type.IsEmpty)
                {
                    var xmlType = ResolveXmlSchemaType(part.Type, set);
                    if (xmlType != null)
                    {
                        return CreateSchemaForType(xmlType, set, schemas, processed);
                    }
                }
            }
            catch
            {
                // fall through
            }

            return new Dictionary<string, object?> { ["type"] = "string" };
        }

        // Create schema for a type: simple -> simple mapping; complex wrapper with single sequence child -> array of inner;
        // otherwise ensure component $ref is created and returned
        private static Dictionary<string, object?> CreateSchemaForType(XmlSchemaType? type, XmlSchemaSet set, Dictionary<string, object?> schemas, Dictionary<XmlQualifiedName, string> processed)
        {
            if (type == null)
            {
                return new Dictionary<string, object?> { ["type"] = "string" };
            }

            // simple types
            if (IsSimpleType(type))
            {
                return MapSimpleTypeToOpenApi(type);
            }

            // complex types: check wrapper pattern (single sequence child)
            if (type is XmlSchemaComplexType ct)
            {
                var inner = GetSingleSequenceChild(ct);
                if (inner != null)
                {
                    var innerType = inner.ElementSchemaType ?? ResolveXmlSchemaType(inner.SchemaTypeName, set);
                    if (innerType != null)
                    {
                        var itemSchema = CreateSchemaForType(innerType, set, schemas, processed);
                        return CreateArraySchema(itemSchema);
                    }
                }

                // fallback: create component for complex type
                var compName = EnsureComponentForType(type, set, schemas, processed);
                return new Dictionary<string, object?> { ["$ref"] = $"#/components/schemas/{compName}" };
            }

            // fallback
            return new Dictionary<string, object?> { ["type"] = "string" };
        }

        // Loads WSDLs (supports file://), merges descriptions and returns grouped schemas
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

        // Read text from a uri (file:// or http(s) supported)
        private static async Task<string> ReadTextFromUriAsync(Uri uri)
        {
            if (uri.IsFile)
            {
                return await File.ReadAllTextAsync(uri.LocalPath);
            }
            return await HttpClient.GetStringAsync(uri);
        }

        // Add schema and resolve includes/imports recursively using schemaLocation resolved against baseUri.
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

        private static Dictionary<string, object?> CreateSimpleResponse(Dictionary<string, object?> schema)
        {
            return new Dictionary<string, object?>
            {
                ["description"] = "Successful response",
                ["content"] = new Dictionary<string, object?>
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = schema
                    }
                }
            };
        }

        private static Dictionary<string, object?> CreateQueryParameter(string name, Dictionary<string, object?> schema)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = name,
                ["in"] = "query",
                ["required"] = false,
                ["schema"] = schema
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

        private static string EnsureComponentForType(XmlSchemaType type, XmlSchemaSet set, Dictionary<string, object?> schemas, Dictionary<XmlQualifiedName, string> processed)
        {
            var qn = type.QualifiedName;
            // If anonymous type, generate a name based on its content
            var compName = !qn.IsEmpty ? qn.Name : $"AnonType_{processed.Count + 1}";

            if (processed.TryGetValue(qn, out var existing)) return existing;

            var schemaObj = new Dictionary<string, object?>();
            schemaObj["type"] = "object";
            var props = new Dictionary<string, object?>();

            // Handle complex type sequences, choices and extensions
            if (type is XmlSchemaComplexType ct)
            {
                // If complexContent with extension, process base and extension items
                if (ct.ContentModel is XmlSchemaComplexContent complexContent && complexContent.Content is XmlSchemaComplexContentExtension extension)
                {
                    // process base type first
                    if (!extension.BaseTypeName.IsEmpty)
                    {
                        var baseType = ResolveXmlSchemaType(extension.BaseTypeName, set);
                        if (baseType != null && !IsSimpleType(baseType))
                        {
                            var baseName = EnsureComponentForType(baseType, set, schemas, processed);
                            // include base properties by reference (simplified)
                            props[$"_extends_{extension.BaseTypeName.Name}"] = new Dictionary<string, object?> { ["$ref"] = $"#/components/schemas/{baseName}" };
                        }
                    }

                    if (extension.Particle is XmlSchemaSequence extSeq)
                    {
                        foreach (XmlSchemaObject item in extSeq.Items)
                        {
                            if (item is XmlSchemaElement child)
                            {
                                AddPropertyForChild(child, set, props, schemas, processed);
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
                            AddPropertyForChild(child, set, props, schemas, processed);
                        }
                    }
                }
                else if (ct.Particle is XmlSchemaChoice choice)
                {
                    // represent choice as optional properties (best-effort)
                    foreach (XmlSchemaObject item in choice.Items)
                    {
                        if (item is XmlSchemaElement child)
                        {
                            AddPropertyForChild(child, set, props, schemas, processed);
                        }
                    }
                }
            }

            schemaObj["properties"] = props;
            schemas[compName] = schemaObj;
            processed[qn] = compName;
            return compName;
        }

        private static void AddPropertyForChild(XmlSchemaElement child, XmlSchemaSet set, Dictionary<string, object?> props, Dictionary<string, object?> schemas, Dictionary<XmlQualifiedName, string> processed)
        {
            var childType = child.ElementSchemaType ?? ResolveXmlSchemaType(child.SchemaTypeName, set) ?? child.ElementSchemaType!;
            if (IsSimpleType(childType))
            {
                props[child.Name ?? child.QualifiedName.Name] = MapSimpleTypeToOpenApi(childType);
            }
            else
            {
                var nestedName = EnsureComponentForType(childType, set, schemas, processed);
                props[child.Name ?? child.QualifiedName.Name] = new Dictionary<string, object?> { ["$ref"] = $"#/components/schemas/{nestedName}" };
            }
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

        private static Dictionary<string, object?> MapSimpleTypeToOpenApi(XmlSchemaType? type)
        {
            var res = new Dictionary<string, object?> { ["type"] = "string" };

            if (type == null) return res;

            var qn = type.QualifiedName;
            if (qn.Namespace == XmlSchema.Namespace)
            {
                switch (qn.Name)
                {
                    case "string":
                    case "normalizedString":
                        res["type"] = "string";
                        break;
                    case "boolean":
                        res["type"] = "boolean";
                        break;
                    case "int":
                    case "integer":
                    case "short":
                    case "byte":
                        res["type"] = "integer";
                        res["format"] = "int32";
                        break;
                    case "long":
                        res["type"] = "integer";
                        res["format"] = "int64";
                        break;
                    case "decimal":
                    case "double":
                    case "float":
                        res["type"] = "number";
                        break;
                    case "dateTime":
                        res["type"] = "string";
                        res["format"] = "date-time";
                        break;
                    case "date":
                        res["type"] = "string";
                        res["format"] = "date";
                        break;
                    case "base64Binary":
                        res["type"] = "string";
                        res["format"] = "byte";
                        break;
                    default:
                        res["type"] = "string";
                        break;
                }
            }

            return res;
        }

        private static string Sanitize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            return new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        }
    }
}