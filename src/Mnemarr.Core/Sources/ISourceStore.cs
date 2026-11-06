namespace Mnemarr.Core.Sources;

/// <summary>The declared sources, kept in the database.</summary>
public interface ISourceStore
{
    IReadOnlyList<Source> List();

    Source? Get(Guid id);

    /// <summary>Adds a source. Refuses with <see cref="Problem.Conflict"/> when its name is already taken.</summary>
    void Add(Source source);

    /// <summary>Changes the name, the mode and the folder of a source (never its kind). False when there was none; a name already taken is a conflict.</summary>
    bool Update(Source source);

    /// <summary>Removes a source with its notes and chunks. False when there was none.</summary>
    bool Remove(Guid id);
}
