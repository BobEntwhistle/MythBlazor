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
        private const string SampleWsdl = @"<?xml version=""1.0"" encoding=""utf-8""?>
<wsdl:definitions xmlns:wsdl=""http://schemas.xmlsoap.org/wsdl/""
                  xmlns:tns=""http://example.com/test""
                  xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
                  targetNamespace=""http://example.com/test"">
  <wsdl:types>
    <xsd:schema targetNamespace=""http://example.com/test"" elementFormDefault=""qualified"">
      <xsd:element name=""GetListRequest"">
        <xsd:complexType>
          <xsd:sequence>
            <xsd:element name=""items"">
              <xsd:complexType>
                <xsd:sequence>
                  <xsd:element name=""item"" type=""xsd:string"" maxOccurs=""unbounded""/>
                </xsd:sequence>
              </xsd:complexType>
            </xsd:element>
          </xsd:sequence>
        </xsd:complexType>
      </xsd:element>

      <xsd:element name=""GetListResponse"">
        <xsd:complexType>
          <xsd:sequence>
            <xsd:element name=""results"">
              <xsd:complexType>
                <xsd:sequence>
                  <xsd:element name=""result"" type=""xsd:string"" maxOccurs=""unbounded""/>
                </xsd:sequence>
              </xsd:complexType>
            </xsd:element>
          </xsd:sequence>
        </xsd:complexType>
      </xsd:element>
    </xsd:schema>
  </wsdl:types>

  <wsdl:message name=""GetListRequestMessage"">
    <wsdl:part name=""parameters"" element=""tns:GetListRequest"" />
  </wsdl:message>

  <wsdl:message name=""GetListResponseMessage"">
    <wsdl:part name=""parameters"" element=""tns:GetListResponse"" />
  </wsdl:message>

  <wsdl:portType name=""MyPortType"">
    <wsdl:operation name=""GetList"">
      <wsdl:input message=""tns:GetListRequestMessage"" />
      <wsdl:output message=""tns:GetListResponseMessage"" />
    </wsdl:operation>
  </wsdl:portType>

  <wsdl:binding name=""MyBinding"" type=""tns:MyPortType"">
    <soap:binding xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/"" transport=""http://schemas.xmlsoap.org/soap/http""/>
    <wsdl:operation name=""GetList"">
      <soap:operation xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/"" soapAction=""urn:GetList"" />
      <wsdl:input>
        <soap:body xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/"" use=""literal""/>
      </wsdl:input>
      <wsdl:output>
        <soap:body xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/"" use=""literal""/>
      </wsdl:output>
    </wsdl:operation>
  </wsdl:binding>

  <wsdl:service name=""MyService"">
    <wsdl:port name=""MyPort"" binding=""tns:MyBinding"">
      <soap:address xmlns:soap=""http://schemas.xmlsoap.org/wsdl/soap/"" location=""http://localhost/""/>
    </wsdl:port>
  </wsdl:service>
</wsdl:definitions>";

        [Fact]
        public async Task GenerateOpenApiFromWsdlAsync_ProducesArrayForWrapperSequence()
        {
            // write the WSDL to a temporary file and use a file:// URL
            var tempWsdlPath = Path.Combine(Path.GetTempPath(), $"sample_wsdl_{Guid.NewGuid():N}.wsdl");
            await File.WriteAllTextAsync(tempWsdlPath, SampleWsdl, Encoding.UTF8);

            try
            {
                var fileUri = new Uri(tempWsdlPath).AbsoluteUri;
                var converter = new WsdlToOpenApiConverter();
                var openApiJson = await converter.GenerateOpenApiFromWsdlAsync(fileUri);

                // Basic assertions: result contains array schema for parameter and response items
                Assert.False(string.IsNullOrWhiteSpace(openApiJson));
                // Normalize whitespace for simpler contains checks
                var normalized = openApiJson.Replace(" ", "").Replace("\r", "").Replace("\n", "");

                // Expect an array type for the parameter or for the response
                Assert.True(normalized.Contains("\"type\":\"array\""), "Expected an array schema in generated OpenAPI.");
                Assert.True(normalized.Contains("\"items\":{") || normalized.Contains("\"items\":[") , "Expected items property for array.");
                Assert.Contains("\"string\"", normalized); // inner simple type should be string
            }
            finally 
            {
                try { File.Delete(tempWsdlPath); } catch { }
            }
        }

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
    }
}