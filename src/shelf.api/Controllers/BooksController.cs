using Microsoft.AspNetCore.Mvc;
using shelf.data;
using shelf.data.Repositories;
using shelf.api.Services;

namespace shelf.api.Controllers;

/// <summary>
/// The sample resource's endpoints. This is the file a scaffolded project edits first.
/// </summary>
/// <remarks>
/// The attributes are not decoration: <c>[Route("[controller]")]</c> puts this at /Books, the
/// <c>Name</c> on <see cref="Get"/> becomes the OpenAPI operationId, and
/// <c>[ProducesResponseType&lt;T&gt;]</c> is what puts a schema rather than a bare 200 in the
/// document. Both front ends' types are generated from that document, so an endpoint that skips
/// them produces a client with no types. See docs/ADDING-A-RESOURCE.md.
/// </remarks>
[ApiController]
[Route("[controller]")]
public class BooksController(
    IRepository<Book> books,
    HarnessFaultInjector faults,
    ILogger<BooksController> logger) : ControllerBase
{
    [HttpGet(Name = "GetBooks")]
    [EndpointSummary("List every book")]
    [ProducesResponseType<IEnumerable<Book>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IEnumerable<Book>>> Get(CancellationToken cancellationToken)
    {
        // The injected-fault check is how both front ends' error banners and spinners get
        // exercised without unplugging the network. Inert unless /harness/* has armed it. It runs
        // before the read on purpose: an armed fault should not depend on the database being up.
        if (await faults.ShouldFailAsync(cancellationToken))
        {
            logger.LogWarning("Failing GET /Books because a fault was injected.");

            return Problem(
                detail: "Injected failure from the test harness.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // Read, not generate. The rows are seeded by BookSeeder at startup, so this returns the
        // same set every time until the seed goes stale.
        return Ok(await books.GetAllAsync(cancellationToken));
    }
}
