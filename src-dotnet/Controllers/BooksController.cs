using Backend.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>Тестовый API: список книг (данные заданы в коде).</summary>
[ApiController]
[Route("api/[controller]")]
public sealed class BooksController : ControllerBase
{
    private static readonly IReadOnlyList<Book> SampleBooks =
    [
        new Book(1, "The Mythical Man-Month", "Frederick P. Brooks Jr.", 1975),
    ];

    /// <summary>Возвращает коллекцию тестовых книг.</summary>
    [HttpGet]
    [Produces("application/json")]
    public ActionResult<IReadOnlyList<Book>> GetAll()
    {
        return Ok(SampleBooks);
    }
}
