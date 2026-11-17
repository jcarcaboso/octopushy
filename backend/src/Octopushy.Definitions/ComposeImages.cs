using System.Text;
using System.Text.Json;
using Octopushy.Definitions.Yaml;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Octopushy.Definitions;

/// <summary>
/// The container images a service's compose file names, for showing what a run changes. A convention of the server, not
/// of any workflow: the first of <see cref="FileNames"/> in the service's folder. Only <c>services.&lt;name&gt;.image</c> is
/// read, as written: no interpolation, <c>extends</c>, <c>include</c>, merge keys or build contexts. The file is
/// repository content, so it is read as events, never built into a tree, and an alias is never expanded beyond the one
/// scalar it names.
/// </summary>
public static class ComposeImages
{
    public static readonly IReadOnlyList<string> FileNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    /// <summary>The service's compose file among a commit's <paramref name="paths"/>: the first of <see cref="FileNames"/> in <paramref name="directory"/>.</summary>
    public static string? FileIn(string directory, IEnumerable<string> paths)
    {
        var present = paths.ToHashSet(StringComparer.Ordinal);
        return FileNames.Select(name => $"{directory}/{name}").FirstOrDefault(present.Contains);
    }

    /// <summary>A larger file is not read.</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>A file with more services is not read.</summary>
    public const int MaxServices = 50;

    /// <summary>A service name or image reference longer than this, or with a control character, counts as no image.</summary>
    public const int MaxReferenceLength = 300;

    /// <summary>
    /// Each service that names an image string, by name. Null when the file is too large, is not YAML, is not a mapping,
    /// has a <c>services</c> that is not a mapping, has more than <see cref="MaxServices"/> services, nests deeper than
    /// <see cref="MaxDepth"/>, or repeats a key that matters here. Never throws for what the text holds.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Read(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
            return null;
        try
        {
            return new Reader(new Parser(new StringReader(text))).Read();
        }
        catch (Exception)
        {
            // Whatever the parser makes of hostile text, the answer is the same: nothing to show.
            return null;
        }
    }

    /// <summary>
    /// The parser's time grows with the square of how deeply collections nest: forty thousand <c>[</c> take seconds.
    /// Reading stops, and shows nothing, at a collection nested deeper than this.
    /// </summary>
    private const int MaxDepth = 32;

    private sealed class Reader(IParser parser)
    {
        /// <summary>Each anchor's text as an image would read it, or null when it is not a string.</summary>
        private readonly Dictionary<string, string?> _anchors = new(StringComparer.Ordinal);

        public Dictionary<string, string>? Read()
        {
            parser.Consume<StreamStart>();
            if (!parser.TryConsume<DocumentStart>(out _))
                return [];
            if (!parser.TryConsume<MappingStart>(out _))
                return null;

            Dictionary<string, string>? images = null;
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (Key() is not { } key)
                    return null;
                if (key != "services")
                {
                    Skip();
                    continue;
                }

                if (images is not null || !parser.TryConsume<MappingStart>(out _))
                    return null;
                images = Services();
                if (images is null)
                    return null;
            }

            return images ?? [];
        }

        private Dictionary<string, string>? Services()
        {
            var images = new Dictionary<string, string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (Key() is not { } name || !names.Add(name) || names.Count > MaxServices)
                    return null;
                if (!parser.TryConsume<MappingStart>(out _))
                {
                    Skip();
                    continue;
                }

                string? image = null;
                var seen = false;
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    if (Key() is not { } key)
                        return null;
                    if (key != "image")
                    {
                        Skip();
                        continue;
                    }

                    if (seen)
                        return null;
                    seen = true;
                    image = Value();
                }

                if (Shown(name) && image is not null && Shown(image))
                    images[name] = image;
            }

            return images;
        }

        /// <summary>A mapping key, which must be a scalar.</summary>
        private string? Key()
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                return null;
            Remember(scalar);
            return scalar.Value;
        }

        /// <summary>A value as an image reads it: a string scalar, or an alias of one. Anything else is consumed and is null.</summary>
        private string? Value()
        {
            if (parser.TryConsume<Scalar>(out var scalar))
                return Remember(scalar);
            if (parser.TryConsume<AnchorAlias>(out var alias))
                return _anchors.GetValueOrDefault(alias.Value.Value);
            Skip();
            return null;
        }

        /// <summary>Consumes one node and everything in it, without recursion, remembering the anchors it defines.</summary>
        private void Skip()
        {
            var depth = 0;
            do
            {
                switch (parser.Consume<ParsingEvent>())
                {
                    case Scalar scalar:
                        Remember(scalar);
                        break;
                    case NodeEvent collection and (MappingStart or SequenceStart):
                        if (!collection.Anchor.IsEmpty)
                            _anchors[collection.Anchor.Value] = null;
                        if (++depth > MaxDepth)
                            throw new YamlException("The file nests too deeply.");
                        break;
                    case MappingEnd or SequenceEnd:
                        depth--;
                        break;
                }
            }
            while (depth > 0);
        }

        private string? Remember(Scalar scalar)
        {
            var text = Text(scalar);
            if (!scalar.Anchor.IsEmpty)
                _anchors[scalar.Anchor.Value] = text;
            return text;
        }

        /// <summary>The scalar's text when the YAML 1.2 core schema reads it as a string, as definitions are read.</summary>
        private static string? Text(Scalar scalar)
        {
            try
            {
                var node = YamlDocumentReader.ConvertScalar(new YamlScalarNode(scalar.Value) { Tag = scalar.Tag, Style = scalar.Style });
                return node?.GetValueKind() == JsonValueKind.String ? scalar.Value : null;
            }
            catch (YamlReadException)
            {
                return null;
            }
        }

        private static bool Shown(string value) =>
            value.Length is > 0 and <= MaxReferenceLength && !value.Any(char.IsControl);
    }
}
