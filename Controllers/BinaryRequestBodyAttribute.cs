using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Documents a request body the action reads itself from <c>Request.Body</c>, such as a zip, so the
    /// OpenAPI document (the REST route contract) shows it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class BinaryRequestBodyAttribute(string contentType) : Attribute
    {
        public string ContentType { get; } = contentType;

        /// <summary>The OpenAPI operation transformer that adds the body.</summary>
        public static Task Apply(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            var attribute = context.Description.ActionDescriptor.EndpointMetadata.OfType<BinaryRequestBodyAttribute>().FirstOrDefault();
            if (attribute != null)
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        [attribute.ContentType] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
                    }
                };
            }
            return Task.CompletedTask;
        }
    }
}
