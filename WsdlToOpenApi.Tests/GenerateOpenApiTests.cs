using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using WsdlToOpenApi;

namespace WsdlToOpenApi.Tests
{
    public class GenerateOpenApiTests
    {
        [Fact]
        public async Task GenerateOpenApiFromVideoServicesWsdl_File_ExecutesAndProducesContent()
        {
            // locate TestData/VideoServices.wsdl relative to test output or repository tree
            string expectedRelative = Path.Combine("TestData", "VideoServices.wsdl");
            string candidate = Path.Combine(AppContext.BaseDirectory, expectedRelative);

            if (!File.Exists(candidate))
            {
                // walk up a few parent directories to find the TestData folder (robust for different test runners)
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    var tryPath = Path.Combine(dir.FullName, "WsdlToOpenApi.Tests", "TestData", "VideoServices.wsdl");
                    if (File.Exists(tryPath))
                    {
                        candidate = tryPath;
                        break;
                    }

                    tryPath = Path.Combine(dir.FullName, "TestData", "VideoServices.wsdl");
                    if (File.Exists(tryPath))
                    {
                        candidate = tryPath;
                        break;
                    }

                    dir = dir.Parent;
                }
            }

            Assert.True(File.Exists(candidate), $"Test data file not found. Tried: {candidate}");

            var fileUri = new Uri(candidate).AbsoluteUri;
            var converter = new WsdlToOpenApiConverter();

            // Execute - test verifies it runs without throwing and returns non-empty content
            var openApiJson = await converter.GenerateOpenApiFromWsdlAsync(fileUri);

            Assert.False(string.IsNullOrWhiteSpace(openApiJson));
            Assert.Contains("\"paths\"", openApiJson);
            Assert.Contains("\"components\"", openApiJson);
        }

        [Fact]
        public async Task GenerateOpenApi_RegistersComponents_ForComplexTypes()
        {
            var wsdl = @"<?xml version=""1.0"" encoding=""utf-8""?>
<wsdl:definitions xmlns:wsdl=""http://schemas.xmlsoap.org/wsdl/""
                  xmlns:tns=""http://example.com/test2""
                  xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
                  xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/""
                  targetNamespace=""http://example.com/test2"">
  <wsdl:types>
    <xsd:schema targetNamespace=""http://example.com/test2"" elementFormDefault=""qualified"">
      <xsd:complexType name=""MyType"">
        <xsd:sequence>
          <xsd:element name=""value"" type=""xsd:string""/>
        </xsd:sequence>
      </xsd:complexType>

      <xsd:element name=""MyRequest"" type=""tns:MyType""/>
      <xsd:element name=""MyResponse"" type=""tns:MyType""/>
    </xsd:schema>
  </wsdl:types>

  <wsdl:message name=""ReqMsg""><wsdl:part name=""parameters"" element=""tns:MyRequest""/></wsdl:message>
  <wsdl:message name=""RespMsg""><wsdl:part name=""parameters"" element=""tns:MyResponse""/></wsdl:message>

  <wsdl:portType name=""MyPort"">
    <wsdl:operation name=""DoIt"">
      <wsdl:input message=""tns:ReqMsg""/>
      <wsdl:output message=""tns:RespMsg""/>
    </wsdl:operation>
  </wsdl:portType>

  <wsdl:binding name=""B"" type=""tns:MyPort"">
    <soap:binding transport=""http://schemas.xmlsoap.org/soap/http""/>
    <wsdl:operation name=""DoIt""><soap:operation soapAction=""urn:DoIt"" /></wsdl:operation>
  </wsdl:binding>

  <wsdl:service name=""S""><wsdl:port name=""P"" binding=""tns:B""><soap:address xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/"" location=""http://localhost/""/></wsdl:port></wsdl:service>
</wsdl:definitions>";

            var temp = Path.Combine(Path.GetTempPath(), $"wsdl_components_{Guid.NewGuid():N}.wsdl");
            await File.WriteAllTextAsync(temp, wsdl, Encoding.UTF8);
            try
            {
                var converter = new WsdlToOpenApiConverter();
                var json = await converter.GenerateOpenApiFromWsdlAsync(new Uri(temp).AbsoluteUri);
                Assert.False(string.IsNullOrWhiteSpace(json));
                Assert.Contains("\"components\"", json);
                Assert.Contains("\"MyType\"", json);
                // We don't want to explicity add the paramaters prefix to every flat parameter
                Assert.DoesNotContain("\"parameters.\"", json);
                Assert.Contains("\"value\"", json);
                Assert.Contains("\"paths\"", json);
            }
            finally { try { File.Delete(temp); } catch { } }
        }

        [Fact]
        public async Task GenerateOpenApi_AddsPathsAndOperations_ForOperation()
        {
            var wsdl = @"<?xml version=""1.0"" encoding=""utf-8""?>
<definitions xmlns=""http://schemas.xmlsoap.org/wsdl/""
                  xmlns:tns=""http://example.com/test3""
                  xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
                  xmlns:wsaw=""http://www.w3.org/2006/05/addressing/wsdl""
                  xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/""
                  targetNamespace=""http://example.com/test3"">
  <types>
    <xsd:schema targetNamespace=""http://example.com/test3"" elementFormDefault=""qualified"">
      <xsd:element name=""Req"">
        <xsd:complexType>
          <xsd:sequence>
            <xsd:element name=""a"" type=""xsd:string""/>
          </xsd:sequence>
        </xsd:complexType>
      </xsd:element>
      <xsd:element name=""Resp"">
        <xsd:complexType>
          <xsd:sequence>
            <xsd:element name=""b"" type=""xsd:string""/>
          </xsd:sequence>
        </xsd:complexType>
      </xsd:element>
    </xsd:schema>
  </types>

  <message name=""ReqMsg""><part name=""parameters"" element=""tns:Req""/></message>
  <message name=""RespMsg""><part name=""parameters"" element=""tns:Resp""/></message>

  <portType name=""PortX"">
    <operation name=""OpY"">
      <documentation>POST</documentation>
      <input wsaw:Action=""OpY"" message=""tns:ReqMsg""/>
      <output wsaw:Action=""OpYResponse"" message=""tns:RespMsg""/>
    </operation>
  </portType>

  <binding name=""B"" type=""tns:PortX"">
    <soap:binding transport=""http://schemas.xmlsoap.org/soap/http""/>
    <operation name=""OpY"">
        <soap:operation soapAction=""urn:OpY"" style=""document"" /></operation> 
  </binding>

  <service name=""S""><port name=""P"" binding=""tns:B""><soap:address location=""http://localhost/""/></port></service>
</definitions>";

            var temp = Path.Combine(Path.GetTempPath(), $"wsdl_paths_{Guid.NewGuid():N}.wsdl");
            await File.WriteAllTextAsync(temp, wsdl, Encoding.UTF8);
            try
            {
                var converter = new WsdlToOpenApiConverter();
                var json = await converter.GenerateOpenApiFromWsdlAsync(new Uri(temp).AbsoluteUri);
                Assert.False(string.IsNullOrWhiteSpace(json));
                // path uses sanitized portType and operation names
                Assert.Contains("/PortX/OpY", json);
                // operation should be post because documentation said POST
                Assert.Contains("\"post\"", json.ToLowerInvariant());
            }
            finally { try { File.Delete(temp); } catch { } }
        }
    }
}