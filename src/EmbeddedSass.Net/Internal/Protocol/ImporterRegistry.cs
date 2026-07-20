using EmbeddedSass.Importing;

namespace EmbeddedSass.Internal.Protocol;

internal sealed class ImporterRegistry
{
    private readonly Dictionary<uint, ISassImporter> _importers = [];
    private uint _nextId = 1;

    public uint Register(ISassImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        if (importer is not ISassContentImporter and not ISassFileImporter)
        {
            throw new ArgumentException(
                $"Unsupported Sass importer type '{importer.GetType().FullName}'.",
                nameof(importer));
        }

        var id = _nextId++;
        _importers.Add(id, importer);
        return id;
    }

    public bool TryGet(uint id, out ISassImporter? importer) =>
        _importers.TryGetValue(id, out importer);
}
