using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Metadata;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public partial class GraphicsElementLoader(
    TemplateFunctions templateFunctions,
    IFileSystem fileSystem,
    ITemplateDataRepository templateDataRepository,
    ILogger<GraphicsElementLoader> logger)
    : IGraphicsElementLoader
{
    private readonly ConcurrentDictionary<string, string> _yamlCache = new();

    public async Task<GraphicsEngineContext> LoadAll(
        GraphicsEngineContext context,
        List<PlayoutItemGraphicsElement> elements,
        CancellationToken cancellationToken)
    {
        try
        {
            // Notify persistence store which elements are active for THIS item.
            // This lets GraphicsPersistenceStore detect graphics_off between items
            // by comparing current element IDs vs previous item's element IDs.
            GraphicsPersistenceStore.UpdateActiveElements(elements.Select(e => e.GraphicsElementId).Where(id => id.HasValue).Select(id => id!.Value));

            // get max epg entries
            int epgEntries = await GetMaxEpgEntries(elements, cancellationToken);

            // init template element variables once
            Dictionary<string, object> templateVariables =
                await InitTemplateVariables(context, epgEntries, cancellationToken);

            // subtitles are in separate files, so they need template variables for later processing
            context = context with { TemplateVariables = templateVariables };

            // fully process references (using template variables)
            foreach (PlayoutItemGraphicsElement reference in elements)
            {
                switch (reference.GraphicsElement.Kind)
                {
                    case GraphicsElementKind.Text:
                    {
                        Option<TextGraphicsElement> maybeElement = await LoadText(
                            reference.GraphicsElement.Path,
                            templateVariables,
                            cancellationToken);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load text graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (TextGraphicsElement element in maybeElement)
                        {
                            element.ElementId = reference.GraphicsElementId ?? 0;
                            ApplyVariableOverrides(element, reference.Variables);
                            context.Elements.Add(new TextElementDataContext(element));
                        }

                        break;
                    }
                    case GraphicsElementKind.Image:
                    {
                        Option<ImageGraphicsElement> maybeElement = await LoadImage(
                            reference.GraphicsElement.Path,
                            templateVariables,
                            cancellationToken);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load image graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (ImageGraphicsElement element in maybeElement)
                        {
                            element.ElementId = reference.GraphicsElementId ?? 0;
                            ApplyVariableOverrides(element, reference.Variables);
                            context.Elements.Add(new ImageElementDataContext(element));
                        }

                        break;
                    }
                    case GraphicsElementKind.Motion:
                    {
                        Option<MotionGraphicsElement> maybeElement = await LoadMotion(
                            reference.GraphicsElement.Path,
                            templateVariables,
                            cancellationToken);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load motion graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (MotionGraphicsElement element in maybeElement)
                        {
                            element.ElementId = reference.GraphicsElementId ?? 0;
                            ApplyVariableOverrides(element, reference.Variables);
                            context.Elements.Add(new MotionElementDataContext(element));
                        }

                        break;
                    }
                    case GraphicsElementKind.Subtitle:
                    {
                        Option<SubtitleGraphicsElement> maybeElement = await LoadSubtitle(
                            reference.GraphicsElement.Path,
                            templateVariables,
                            cancellationToken);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load subtitle graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (SubtitleGraphicsElement element in maybeElement)
                        {
                            var variables = new Dictionary<string, string>();
                            if (!string.IsNullOrWhiteSpace(reference.Variables))
                            {
                                variables = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                                    reference.Variables);
                            }

                            context.Elements.Add(new SubtitleElementDataContext(element, variables));
                        }

                        break;
                    }
                    case GraphicsElementKind.Script:
                    {
                        Option<ScriptGraphicsElement> maybeElement = await LoadScript(
                            reference.GraphicsElement.Path,
                            templateVariables,
                            cancellationToken);
                        if (maybeElement.IsNone)
                        {
                            logger.LogWarning(
                                "Failed to load script graphics element from file {Path}; ignoring",
                                reference.GraphicsElement.Path);
                        }

                        foreach (ScriptGraphicsElement element in maybeElement)
                        {
                            context.Elements.Add(new ScriptElementDataContext(element));
                        }

                        break;
                    }
                    default:
                        logger.LogInformation(
                            "Ignoring unsupported graphics element kind {Kind}",
                            nameof(reference.GraphicsElement.Kind));
                        break;
                }
            }

            return context;
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }

        return null;
    }

    public async Task<Option<string>> TryLoadName(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            string yaml = await ReadFile(fileName, cancellationToken);
            var template = Template.Parse(yaml);

            var builder = new StringBuilder();
            var scriptPage = template.Page;

            if (scriptPage.Body != null)
            {
                foreach (var statement in scriptPage.Body.Statements)
                {
                    if (statement is ScriptRawStatement rawStatement)
                    {
                        builder.Append(rawStatement.Text);
                    }
                }
            }

            Option<BaseGraphicsElement> maybeElement = FromYamlIgnoreUnmatched<BaseGraphicsElement>(builder.ToString());
            foreach (BaseGraphicsElement element in maybeElement.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
            {
                return element.Name;
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return Option<string>.None;
    }

    private async Task<int> GetMaxEpgEntries(List<PlayoutItemGraphicsElement> elements, CancellationToken cancellationToken)
    {
        var epgEntries = 0;

        IEnumerable<PlayoutItemGraphicsElement> elementsWithEpg = elements.Where(e =>
            e.GraphicsElement.Kind is GraphicsElementKind.Text or GraphicsElementKind.Subtitle
                or GraphicsElementKind.Motion or GraphicsElementKind.Script or GraphicsElementKind.Image);

        foreach (var reference in elementsWithEpg)
        {
            try
            {
                string text = await ReadFile(reference.GraphicsElement.Path, cancellationToken);
                foreach (string line in text.Split("\n"))
                {
                    Match match = EpgEntriesRegex().Match(line);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out int value))
                    {
                        continue;
                    }

                    epgEntries = Math.Max(epgEntries, value);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to read graphics element at {Path} for EPG entries",
                    reference.GraphicsElement.Path);
            }
        }

        return epgEntries;
    }

    private Task<Option<ImageGraphicsElement>> LoadImage(
        string fileName,
        Dictionary<string, object> variables,
        CancellationToken cancellationToken) =>
        GetTemplatedYaml(fileName, variables, cancellationToken).BindT(FromYaml<ImageGraphicsElement>);

    private Task<Option<TextGraphicsElement>> LoadText(
        string fileName,
        Dictionary<string, object> variables,
        CancellationToken cancellationToken) =>
        GetTemplatedYaml(fileName, variables, cancellationToken).BindT(FromYaml<TextGraphicsElement>);

    private Task<Option<MotionGraphicsElement>> LoadMotion(
        string fileName,
        Dictionary<string, object> variables,
        CancellationToken cancellationToken) =>
        GetTemplatedYaml(fileName, variables, cancellationToken).BindT(FromYaml<MotionGraphicsElement>);

    private Task<Option<SubtitleGraphicsElement>> LoadSubtitle(
        string fileName,
        Dictionary<string, object> variables,
        CancellationToken cancellationToken) =>
        GetTemplatedYaml(fileName, variables, cancellationToken).BindT(FromYaml<SubtitleGraphicsElement>);

    private Task<Option<ScriptGraphicsElement>> LoadScript(
        string fileName,
        Dictionary<string, object> variables,
        CancellationToken cancellationToken) =>
        GetTemplatedYaml(fileName, variables, cancellationToken).BindT(FromYaml<ScriptGraphicsElement>);

    private async Task<Dictionary<string, object>> InitTemplateVariables(
        GraphicsEngineContext context,
        int epgEntries,
        CancellationToken cancellationToken)
    {
        // common variables
        var result = new Dictionary<string, object>
        {
            [FFmpegProfileTemplateDataKey.Resolution] = context.FrameSize,
            [FFmpegProfileTemplateDataKey.ScaledResolution] = context.SquarePixelFrameSize,
            [FFmpegProfileTemplateDataKey.RFrameRate] = context.FrameRate.RFrameRate,
            [FFmpegProfileTemplateDataKey.FrameRate] = context.FrameRate.ParsedFrameRate,
            [ChannelTemplateDataKey.ChannelStartTime] = context.ChannelStartTime,
            [MediaItemTemplateDataKey.StreamSeek] = context.Seek,
            [MediaItemTemplateDataKey.Start] = context.ContentStartTime,
            [MediaItemTemplateDataKey.Stop] = context.ContentFinishTime,
            ["block_remaining_seconds"] = context.BlockRemainingTime.TotalSeconds
        };

        // media item variables
        Option<Dictionary<string, object>> maybeTemplateData =
            await templateDataRepository.GetMediaItemTemplateData(context.MediaItem, cancellationToken);
        foreach (Dictionary<string, object> templateData in maybeTemplateData)
        {
            foreach (KeyValuePair<string, object> variable in templateData)
            {
                result.Add(variable.Key, variable.Value);
            }
        }

        // epg variables
        if (epgEntries > 0)
        {
            DateTimeOffset startTime = context.ContentStartTime + context.Seek;
            Option<Dictionary<string, object>> maybeEpgData =
                await templateDataRepository.GetEpgTemplateData(context.ChannelNumber, startTime, epgEntries);
            foreach (Dictionary<string, object> templateData in maybeEpgData)
            {
                foreach (KeyValuePair<string, object> variable in templateData)
                {
                    result.Add(variable.Key, variable.Value);
                }
            }
        }

        return result;
    }

    private async Task<Option<TemplatedYaml>> GetTemplatedYaml(
        string fileName,
        Dictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        string yaml = await ReadFile(fileName, cancellationToken);
        try
        {
            var scriptObject = new ScriptObject();
            scriptObject.Import(variables, renamer: member => member.Name);
            scriptObject.Import("convert_timezone", templateFunctions.ConvertTimeZone);
            scriptObject.Import("format_datetime", templateFunctions.FormatDateTime);
            scriptObject.Import("get_directory_name", (string path) => Path.GetDirectoryName(path));
            scriptObject.Import(
                "get_filename_without_extension",
                (string path) => Path.GetFileNameWithoutExtension(path));

            var context = new TemplateContext { MemberRenamer = member => member.Name };
            context.PushGlobal(scriptObject);
            return new TemplatedYaml(
                fileSystem.Path.GetFileName(fileName),
                await Template.Parse(yaml).RenderAsync(context));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to render graphics element YAML definition as scriban template");
            return Option<TemplatedYaml>.None;
        }
    }


    private static void ApplyVariableOverrides(BaseGraphicsElement element, string variablesJson)
    {
        if (string.IsNullOrWhiteSpace(variablesJson)) return;
        try
        {
            var vars = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(variablesJson)
                       ?? new Dictionary<string, string>();

            if (vars.TryGetValue("fade_in_seconds", out string fiStr) &&
                double.TryParse(fiStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double fi))
            {
                switch (element)
                {
                    case ImageGraphicsElement img: img.FadeInSeconds = fi; break;
                    case TextGraphicsElement txt: txt.FadeInSeconds = fi; break;
                    case MotionGraphicsElement mot: mot.FadeInSeconds = fi; break;
                }
            }

            if (vars.TryGetValue("fade_out_seconds", out string foStr) &&
                double.TryParse(foStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double fo))
            {
                switch (element)
                {
                    case ImageGraphicsElement img: img.FadeOutSeconds = fo; break;
                    case TextGraphicsElement txt: txt.FadeOutSeconds = fo; break;
                    case MotionGraphicsElement mot: mot.FadeOutSeconds = fo; break;
                }
            }
        }
        catch { }
    }
    private Option<T> FromYaml<T>(TemplatedYaml yaml) where T : BaseGraphicsElement
    {
        try
        {
            // TODO: validate schema
            // if (await yamlScheduleValidator.ValidateSchedule(yaml, isImport) == false)
            // {
            //     return Option<YamlPlayoutDefinition>.None;
            // }

            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var result = deserializer.Deserialize<T>(yaml.Yaml);
            result.SourceFileName = yaml.SourceFileName;

            // Manually read snake_case properties that CamelCaseNamingConvention cannot
            // map automatically. Read them via regex from the rendered YAML string,
            // the same way GetMaxEpgEntries reads epg_entries.
            if (result is TextGraphicsElement textElement)
            {
                Match textFileMatch = TextFileRegex().Match(yaml.Yaml);
                if (textFileMatch.Success)
                {
                    textElement.TextFile = textFileMatch.Groups[1].Value.Trim().Trim('\'').Trim('"');
                }
            }

            // Inject persistent flag for all element types via regex
            Match persistentMatch = PersistentRegex().Match(yaml.Yaml);
            if (persistentMatch.Success &&
                persistentMatch.Groups[1].Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                switch (result)
                {
                    case ImageGraphicsElement img: img.Persistent = true; break;
                    case TextGraphicsElement txt: txt.Persistent = true; break;
                    case MotionGraphicsElement mot: mot.Persistent = true; break;
                }
            }

            // Inject fade_in_seconds and fade_out_seconds via regex
            Match fadeInMatch = FadeInSecondsRegex().Match(yaml.Yaml);
            if (fadeInMatch.Success && double.TryParse(fadeInMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double fadeIn))
            {
                switch (result)
                {
                    case ImageGraphicsElement img: img.FadeInSeconds = fadeIn; break;
                    case TextGraphicsElement txt: txt.FadeInSeconds = fadeIn; break;
                    case MotionGraphicsElement mot: mot.FadeInSeconds = fadeIn; break;
                }
            }

            Match fadeOutMatch = FadeOutSecondsRegex().Match(yaml.Yaml);
            if (fadeOutMatch.Success && double.TryParse(fadeOutMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double fadeOut))
            {
                switch (result)
                {
                    case ImageGraphicsElement img: img.FadeOutSeconds = fadeOut; break;
                    case TextGraphicsElement txt: txt.FadeOutSeconds = fadeOut; break;
                    case MotionGraphicsElement mot: mot.FadeOutSeconds = fadeOut; break;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load graphics element YAML definition");
            return Option<T>.None;
        }
    }

    private static Option<T> FromYamlIgnoreUnmatched<T>(string yaml)
    {
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            return deserializer.Deserialize<T>(yaml);
        }
        catch (Exception)
        {
            return Option<T>.None;
        }
    }

    private async Task<string> ReadFile(string fileName, CancellationToken cancellationToken)
    {
        string key = fileSystem.Path.GetFullPath(fileName);
        if (_yamlCache.TryGetValue(key, out string cached))
        {
            return cached;
        }

        string yaml = await fileSystem.File.ReadAllTextAsync(key, cancellationToken);
        _yamlCache.TryAdd(key, yaml);

        return yaml;
    }

    [GeneratedRegex(@"epg_entries:\s*(\d+)")]
    private static partial Regex EpgEntriesRegex();

    [GeneratedRegex(@"text_file:\s*'?""?([^'""\n]+)'?""?")]
    private static partial Regex TextFileRegex();

    [GeneratedRegex(@"persistent:\s*(true|false)", RegexOptions.IgnoreCase)]
    private static partial Regex PersistentRegex();

    [GeneratedRegex(@"fade_in_seconds:\s*([0-9]*\.?[0-9]+)")]
    private static partial Regex FadeInSecondsRegex();

    [GeneratedRegex(@"fade_out_seconds:\s*([0-9]*\.?[0-9]+)")]
    private static partial Regex FadeOutSecondsRegex();

    private record TemplatedYaml(string SourceFileName, string Yaml);
}
