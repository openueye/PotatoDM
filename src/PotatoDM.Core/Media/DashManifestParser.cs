using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PotatoDM.Core.Exceptions;

namespace PotatoDM.Core.Media;

/// <summary>
/// Parses an ISO/IEC 23009-1 (MPEG-DASH) MPD into a <see cref="MediaSourcePlan"/>.
/// The returned plan has tracks already selected and segments fully expanded, so
/// the downloader only needs to fetch bytes and concatenate them.
/// </summary>
public static class DashManifestParser
{
    private const string CencScheme = "urn:mpeg:dash:mp4protection:2011";
    private const string UuidPrefix = "urn:uuid:";

    /// <summary>Parses <paramref name="text"/> as an MPD resolved against <paramref name="manifestUri"/>.</summary>
    public static MediaSourcePlan Parse(string text, Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(manifestUri);

        XDocument document;
        try
        {
            document = XDocument.Parse(text);
        }
        catch (XmlException exception)
        {
            throw new MediaManifestException("The DASH manifest is not valid XML.", exception);
        }

        var mpd = document.Root;
        if (mpd is null || !mpd.Name.LocalName.Equals("MPD", StringComparison.Ordinal))
        {
            throw new MediaManifestException("The document root is not an MPD element.");
        }

        if (string.Equals(Attr(mpd, "type"), "dynamic", StringComparison.OrdinalIgnoreCase))
        {
            throw new MediaManifestException(
                "Live (dynamic) DASH manifests are not supported; the manifest must be static.");
        }

        var contentPeriods = Children(mpd, "Period")
            .Where(period => Children(period, "AdaptationSet").Any())
            .ToList();

        if (contentPeriods.Count > 1)
        {
            throw new MediaManifestException(
                "Multi-period DASH manifests are not supported: more than one Period contains AdaptationSets.");
        }

        // DRM that applies to the whole manifest or the whole period.
        RefuseDrm(mpd);
        foreach (var contentPeriod in contentPeriods)
        {
            RefuseDrm(contentPeriod);
        }

        if (contentPeriods.Count == 0)
        {
            throw new MediaManifestException("The DASH manifest contains no video or audio AdaptationSets.");
        }

        var period = contentPeriods[0];
        var mpdBase = AccumulateBase(manifestUri, mpd);
        var periodBase = AccumulateBase(mpdBase, period);

        var warnings = new List<string>();
        var videoCandidates = new List<Candidate>();
        var audioCandidates = new List<Candidate>();

        foreach (var adaptationSet in Children(period, "AdaptationSet"))
        {
            RefuseDrm(adaptationSet);

            var kind = Classify(adaptationSet);
            if (kind is null)
            {
                warnings.Add("Skipped an AdaptationSet that is neither video nor audio (text/subtitles are not downloaded).");
                continue;
            }

            foreach (var representation in Children(adaptationSet, "Representation"))
            {
                var candidate = new Candidate(adaptationSet, representation);
                if (kind == "video")
                {
                    videoCandidates.Add(candidate);
                }
                else
                {
                    audioCandidates.Add(candidate);
                }
            }
        }

        var tracks = new List<MediaTrackPlan>(2);
        MediaTrackPlan? videoTrack = null;
        MediaTrackPlan? audioTrack = null;

        if (videoCandidates.Count > 0)
        {
            videoTrack = BuildTrack(mpd, period, Select(videoCandidates, "video"), "video", periodBase);
        }

        if (audioCandidates.Count > 0)
        {
            audioTrack = BuildTrack(mpd, period, Select(audioCandidates, "audio"), "audio", periodBase);
        }

        if (videoTrack is null && audioTrack is null)
        {
            throw new MediaManifestException("The DASH manifest contains no video or audio AdaptationSets.");
        }

        if (videoTrack is not null)
        {
            tracks.Add(videoTrack);
        }

        if (audioTrack is not null)
        {
            tracks.Add(audioTrack);
        }

        var containerExtension = videoTrack?.ContainerExtension ?? audioTrack?.ContainerExtension ?? ".mp4";

        return new MediaSourcePlan
        {
            Kind = ManifestKind.Dash,
            Tracks = tracks,
            ContainerExtension = containerExtension,
            Warnings = warnings,
            Fingerprint = ComputeFingerprint(text),
        };
    }

    private static string ComputeFingerprint(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static MediaTrackPlan BuildTrack(
        XElement mpd,
        XElement period,
        Candidate candidate,
        string kind,
        Uri periodBase)
    {
        var adaptationSet = candidate.AdaptationSet;
        var representation = candidate.Representation;

        RefuseDrm(representation);

        var adaptationSetBase = AccumulateBase(periodBase, adaptationSet);
        var representationBase = AccumulateBase(adaptationSetBase, representation);

        var mimeType = Attr(representation, "mimeType") ?? Attr(adaptationSet, "mimeType");
        var language = Attr(adaptationSet, "lang");

        return new MediaTrackPlan
        {
            Kind = kind,
            Language = string.IsNullOrWhiteSpace(language) ? null : language,
            Segments = BuildSegments(mpd, period, adaptationSet, representation, representationBase),
            ContainerExtension = ContainerExtension(kind, mimeType),
        };
    }

    private static IReadOnlyList<MediaSegment> BuildSegments(
        XElement mpd,
        XElement period,
        XElement adaptationSet,
        XElement representation,
        Uri representationBase)
    {
        if (FindChild(representation, adaptationSet, period, mpd, "SegmentTemplate") is not null)
        {
            return BuildTemplateSegments(mpd, period, adaptationSet, representation, representationBase);
        }

        if (FindChild(representation, adaptationSet, period, null, "SegmentList") is { } list)
        {
            return BuildListSegments(list, representationBase);
        }

        return BuildFullFileSegments(representationBase);
    }

    private static IReadOnlyList<MediaSegment> BuildTemplateSegments(
        XElement mpd,
        XElement period,
        XElement adaptationSet,
        XElement representation,
        Uri representationBase)
    {
        var representationId = Attr(representation, "id") ?? Attr(adaptationSet, "id") ?? string.Empty;
        var bandwidth = ParseLong(Attr(representation, "bandwidth") ?? Attr(adaptationSet, "bandwidth"));

        var mediaTemplate = InheritedAttr(representation, adaptationSet, period, mpd, "SegmentTemplate", "media");
        var initializationTemplate = InheritedAttr(representation, adaptationSet, period, mpd, "SegmentTemplate", "initialization");
        var timescale = ParseLong(InheritedAttr(representation, adaptationSet, period, mpd, "SegmentTemplate", "timescale") ?? "1");
        if (timescale <= 0)
        {
            timescale = 1;
        }

        var startNumber = ParseLong(InheritedAttr(representation, adaptationSet, period, mpd, "SegmentTemplate", "startNumber") ?? "1");

        MediaInitialization? initialization = null;
        if (!string.IsNullOrWhiteSpace(initializationTemplate))
        {
            var initUri = Resolve(
                Substitute(initializationTemplate, representationId, bandwidth, startNumber, 0),
                representationBase);
            initialization = new MediaInitialization(initUri, null);
        }

        if (string.IsNullOrWhiteSpace(mediaTemplate))
        {
            throw new MediaManifestException("A SegmentTemplate is present but has no media template.");
        }

        if (FindTimeline(representation, adaptationSet, period, mpd) is { } timeline)
        {
            return BuildTimelineSegments(
                mediaTemplate, timeline, representationId, bandwidth, timescale, startNumber, initialization, representationBase);
        }

        var duration = ParseLong(InheritedAttr(representation, adaptationSet, period, mpd, "SegmentTemplate", "duration"));
        var endNumberText = InheritedAttr(representation, adaptationSet, period, mpd, "SegmentTemplate", "endNumber");
        long? endNumber = endNumberText is null ? null : ParseLong(endNumberText);

        long count;
        if (endNumber is { } end)
        {
            count = Math.Max(0, end - startNumber + 1);
        }
        else
        {
            if (duration <= 0)
            {
                throw new MediaManifestException(
                    "The SegmentTemplate has no segment duration and no endNumber, so the segment count cannot be determined.");
            }

            count = (long)Math.Ceiling(TotalDurationSeconds(period, mpd) * timescale / duration);
        }

        var durationSeconds = duration > 0 ? duration / (double)timescale : 0;
        var segments = new List<MediaSegment>((int)Math.Min(count, int.MaxValue));
        var sequence = 1L;

        for (var offset = 0L; offset < count; offset++)
        {
            var number = startNumber + offset;
            var time = (number - startNumber) * duration;
            var uri = Resolve(Substitute(mediaTemplate, representationId, bandwidth, number, time), representationBase);
            segments.Add(new MediaSegment(uri, null, sequence++, durationSeconds, null, initialization, false));
        }

        return segments;
    }

    private static IReadOnlyList<MediaSegment> BuildTimelineSegments(
        string mediaTemplate,
        XElement timeline,
        string representationId,
        long bandwidth,
        long timescale,
        long startNumber,
        MediaInitialization? initialization,
        Uri representationBase)
    {
        var segments = new List<MediaSegment>();
        var sequence = 1L;
        var number = startNumber;
        long? continuation = null;

        foreach (var entry in Children(timeline, "S"))
        {
            var duration = ParseLong(Attr(entry, "d"));
            if (duration <= 0)
            {
                throw new MediaManifestException("A SegmentTimeline S element has an invalid duration.");
            }

            var repeatText = Attr(entry, "r");
            var repeat = repeatText is null ? 0 : ParseLong(repeatText);
            if (repeat == -1)
            {
                throw new MediaManifestException(
                    "A SegmentTimeline S element uses r=\"-1\" (repeat until the next Period), which is not supported.");
            }

            var startText = Attr(entry, "t");
            var start = startText is null ? continuation ?? 0 : ParseLong(startText);
            var durationSeconds = duration / (double)timescale;

            for (var step = 0L; step <= repeat; step++)
            {
                var time = start + step * duration;
                var uri = Resolve(Substitute(mediaTemplate, representationId, bandwidth, number, time), representationBase);
                segments.Add(new MediaSegment(uri, null, sequence++, durationSeconds, null, initialization, false));
                number++;
            }

            continuation = start + duration * (repeat + 1);
        }

        return segments;
    }

    private static IReadOnlyList<MediaSegment> BuildListSegments(XElement list, Uri representationBase)
    {
        MediaInitialization? initialization = null;
        if (Children(list, "Initialization").FirstOrDefault() is { } init &&
            !string.IsNullOrWhiteSpace(Attr(init, "sourceURL")))
        {
            var initUri = Resolve(Attr(init, "sourceURL")!, representationBase);
            initialization = new MediaInitialization(initUri, ParseRange(Attr(init, "range")));
        }

        var segments = new List<MediaSegment>();
        var sequence = 1L;

        foreach (var segmentUrl in Children(list, "SegmentURL"))
        {
            var media = Attr(segmentUrl, "media");
            if (string.IsNullOrWhiteSpace(media))
            {
                continue;
            }

            var uri = Resolve(media, representationBase);
            var range = ParseRange(Attr(segmentUrl, "mediaRange"));
            segments.Add(new MediaSegment(uri, range, sequence++, 0, null, initialization, false));
        }

        return segments;
    }

    private static IReadOnlyList<MediaSegment> BuildFullFileSegments(Uri representationBase)
    {
        if (!representationBase.IsAbsoluteUri)
        {
            throw new MediaManifestException(
                "The Representation has no BaseURL and the manifest URL does not resolve to an absolute media URI.");
        }

        return new[] { new MediaSegment(representationBase, null, 1, 0, null, null, false) };
    }

    private static string? Classify(XElement adaptationSet)
    {
        var contentType = Attr(adaptationSet, "contentType");
        var mimeType = Attr(adaptationSet, "mimeType")
            ?? Children(adaptationSet, "Representation")
                .Select(representation => Attr(representation, "mimeType"))
                .FirstOrDefault(value => value is not null);

        if (string.Equals(contentType, "video", StringComparison.OrdinalIgnoreCase) ||
            (mimeType is not null && mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
        {
            return "video";
        }

        if (string.Equals(contentType, "audio", StringComparison.OrdinalIgnoreCase) ||
            (mimeType is not null && mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)))
        {
            return "audio";
        }

        return null;
    }

    private static Candidate Select(IReadOnlyList<Candidate> candidates, string kind)
    {
        return candidates
            .OrderByDescending(candidate => ParseLong(Attr(candidate.Representation, "bandwidth") ?? Attr(candidate.AdaptationSet, "bandwidth")))
            .ThenByDescending(candidate => kind == "video" ? ParseLong(Attr(candidate.Representation, "height")) : 0)
            .ThenByDescending(candidate => kind == "video" ? ParseLong(Attr(candidate.Representation, "width")) : 0)
            .First();
    }

    private static string ContainerExtension(string kind, string? mimeType)
    {
        var isWebm = mimeType is not null && mimeType.Contains("webm", StringComparison.OrdinalIgnoreCase);
        return kind == "video"
            ? isWebm ? ".webm" : ".mp4"
            : isWebm ? ".weba" : ".m4a";
    }

    private static double TotalDurationSeconds(XElement period, XElement mpd)
    {
        var text = Attr(period, "duration") ?? Attr(mpd, "mediaPresentationDuration");
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new MediaManifestException(
                "Cannot determine the segment count: the manifest provides no Period@duration or MPD@mediaPresentationDuration.");
        }

        return ParseDurationSeconds(text);
    }

    private static double ParseDurationSeconds(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || value[0] != 'P')
        {
            throw new MediaManifestException($"Invalid ISO-8601 duration '{text}'.");
        }

        var total = 0.0;
        var inTime = false;
        var index = 1;

        while (index < value.Length)
        {
            var numberStart = index;
            while (index < value.Length && (char.IsDigit(value[index]) || value[index] is '.' or ','))
            {
                index++;
            }

            if (index == numberStart)
            {
                if (index < value.Length && value[index] == 'T')
                {
                    inTime = true;
                    index++;
                    continue;
                }

                throw new MediaManifestException($"Invalid ISO-8601 duration '{text}'.");
            }

            var numberText = value[numberStart..index].Replace(',', '.');
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                throw new MediaManifestException($"Invalid ISO-8601 duration '{text}'.");
            }

            if (index >= value.Length)
            {
                throw new MediaManifestException($"Invalid ISO-8601 duration '{text}'.");
            }

            var unit = value[index];
            index++;

            total += unit switch
            {
                'Y' => amount * 365.25 * 86400,
                'M' => amount * (inTime ? 60 : 30.4375 * 86400),
                'W' => amount * 7 * 86400,
                'D' => amount * 86400,
                'H' => amount * 3600,
                'S' => amount,
                _ => throw new MediaManifestException($"Invalid ISO-8601 duration '{text}'."),
            };
        }

        return total;
    }

    private static void RefuseDrm(XElement element)
    {
        foreach (var protection in Children(element, "ContentProtection"))
        {
            var scheme = Attr(protection, "schemeIdUri");
            if (string.IsNullOrWhiteSpace(scheme))
            {
                continue;
            }

            if (scheme.Equals(CencScheme, StringComparison.OrdinalIgnoreCase))
            {
                throw new DrmProtectedException("CENC");
            }

            if (scheme.StartsWith(UuidPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new DrmProtectedException(scheme);
            }
        }
    }

    private static string Substitute(string template, string representationId, long bandwidth, long number, long time)
    {
        var builder = new StringBuilder(template.Length);
        var index = 0;

        while (index < template.Length)
        {
            var dollar = template.IndexOf('$', index);
            if (dollar < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, dollar - index);

            var close = template.IndexOf('$', dollar + 1);
            if (close < 0)
            {
                builder.Append(template, dollar, template.Length - dollar);
                break;
            }

            var token = template[(dollar + 1)..close];
            if (token.Length == 0)
            {
                builder.Append('$');
            }
            else
            {
                var percent = token.IndexOf('%');
                var name = percent < 0 ? token : token[..percent];
                var format = percent < 0 ? null : token[(percent + 1)..];

                var value = name switch
                {
                    "RepresentationID" => representationId,
                    "Bandwidth" => bandwidth.ToString(CultureInfo.InvariantCulture),
                    "Number" => number.ToString(CultureInfo.InvariantCulture),
                    "Time" => time.ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };

                if (value is null)
                {
                    builder.Append(template, dollar, close - dollar + 1);
                }
                else
                {
                    builder.Append(ApplyPrintf(value, format));
                }
            }

            index = close + 1;
        }

        return builder.ToString();
    }

    private static string ApplyPrintf(string value, string? format)
    {
        if (string.IsNullOrEmpty(format) || format == "d")
        {
            return value;
        }

        if (!format.EndsWith('d'))
        {
            return value;
        }

        var widthText = format[..^1];
        var zeroPad = widthText.StartsWith('0');
        if (!int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width <= 0)
        {
            return value;
        }

        return zeroPad ? value.PadLeft(width, '0') : value.PadLeft(width, ' ');
    }

    private static Uri AccumulateBase(Uri parent, XElement element)
    {
        var result = parent;
        foreach (var baseUrl in Children(element, "BaseURL"))
        {
            var value = baseUrl.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            result = Resolve(value, result);
        }

        return result;
    }

    private static Uri Resolve(string reference, Uri baseUri) => new(baseUri, reference);

    private static ByteRange? ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var dash = text.IndexOf('-');
        if (dash < 0 ||
            !long.TryParse(text[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(text[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            end < start)
        {
            throw new MediaManifestException($"Invalid byte range '{value}'.");
        }

        return new ByteRange(start, end - start + 1);
    }

    private static long ParseLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static string? Attr(XElement? element, string localName)
    {
        return element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.Ordinal))?.Value;
    }

    private static IEnumerable<XElement> Children(XElement? element, string localName)
    {
        return element is null
            ? Enumerable.Empty<XElement>()
            : element.Elements().Where(child => child.Name.LocalName.Equals(localName, StringComparison.Ordinal));
    }

    private static XElement? FindChild(XElement? a, XElement? b, XElement? c, XElement? d, string localName)
    {
        return Children(a, localName).FirstOrDefault()
            ?? Children(b, localName).FirstOrDefault()
            ?? Children(c, localName).FirstOrDefault()
            ?? Children(d, localName).FirstOrDefault();
    }

    private static string? InheritedAttr(
        XElement representation,
        XElement adaptationSet,
        XElement period,
        XElement mpd,
        string elementName,
        string attributeName)
    {
        foreach (var level in new[] { representation, adaptationSet, period, mpd })
        {
            var element = Children(level, elementName).FirstOrDefault();
            var value = Attr(element, attributeName);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static XElement? FindTimeline(XElement representation, XElement adaptationSet, XElement period, XElement mpd)
    {
        foreach (var level in new[] { representation, adaptationSet, period, mpd })
        {
            var template = Children(level, "SegmentTemplate").FirstOrDefault();
            var timeline = Children(template, "SegmentTimeline").FirstOrDefault();
            if (timeline is not null)
            {
                return timeline;
            }
        }

        return null;
    }

    private readonly record struct Candidate(XElement AdaptationSet, XElement Representation);
}
