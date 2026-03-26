namespace Backend.Models;

/// <summary>Модель для тестового API книг.</summary>
public sealed record Book(int Id, string Title, string Author, int YearPublished);
