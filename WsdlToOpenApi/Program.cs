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
}