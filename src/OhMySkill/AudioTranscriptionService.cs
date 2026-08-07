using System.Speech.Recognition;

namespace OhMySkill;

public sealed record TranscriptionResult(
    string Text,
    IReadOnlyList<TranscriptSegment> Segments,
    string Source);

public static class LocalSpeechTranscriber
{
    public static async Task<string?> TryTranscribeAsync(byte[] wav, CancellationToken cancellationToken = default)
    {
        var result = await TryTranscribeDetailedAsync(wav, cancellationToken);
        return result?.Text;
    }

    public static async Task<TranscriptionResult?> TryTranscribeDetailedAsync(byte[] wav, CancellationToken cancellationToken = default)
    {
        if (wav.Length == 0) return null;
        try
        {
            return await Task.Run(() => Transcribe(wav), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static TranscriptionResult? Transcribe(byte[] wav)
    {
        using var stream = new MemoryStream(wav, writable: false);
        using var engine = new SpeechRecognitionEngine();
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToWaveStream(stream);
        var segments = new List<TranscriptSegment>();
        var cursor = 0L;
        RecognitionResult? result;
        while ((result = engine.Recognize()) is not null)
        {
            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var start = result.Audio is null ? cursor : Math.Max(0, (long)result.Audio.AudioPosition.TotalMilliseconds);
            var duration = result.Audio is null ? EstimateDuration(text) : Math.Max(1, (long)result.Audio.Duration.TotalMilliseconds);
            var end = Math.Max(start + 1, start + duration);
            segments.Add(new TranscriptSegment(start, end, text, Math.Clamp(result.Confidence, 0, 1), "Windows Speech", "provider-segment"));
            cursor = end;
        }

        if (segments.Count == 0) return null;
        return new TranscriptionResult(string.Join(" ", segments.Select(segment => segment.Text)), segments, "Windows Speech");
    }

    private static long EstimateDuration(string text) => Math.Max(1000, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 360L);
}

public static class TranscriptSegmenter
{
    public static IReadOnlyList<TranscriptSegment> Split(
        string text,
        long durationMilliseconds,
        double confidence,
        string source = "provider",
        string timing = "estimated-window")
    {
        var sentences = text.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sentences.Length == 0) return [];
        var chunks = sentences.SelectMany(sentence => ChunkWords(sentence, 12)).Where(chunk => chunk.Length > 0).ToArray();
        if (chunks.Length == 0) return [];

        var duration = Math.Max(1000, durationMilliseconds);
        var totalWeight = Math.Max(1, chunks.Sum(chunk => chunk.Length));
        var cursor = 0L;
        var result = new List<TranscriptSegment>(chunks.Length);
        for (var index = 0; index < chunks.Length; index++)
        {
            var start = cursor;
            var end = index == chunks.Length - 1
                ? duration
                : Math.Clamp(start + Math.Max(250, (long)Math.Round(duration * (chunks[index].Length / (double)totalWeight))), start + 1, duration);
            result.Add(new TranscriptSegment(start, end, chunks[index], Math.Clamp(confidence, 0, 1), source, timing));
            cursor = end;
        }
        return result;
    }

    private static IEnumerable<string> ChunkWords(string sentence, int wordsPerChunk)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < words.Length; index += wordsPerChunk)
            yield return string.Join(' ', words.Skip(index).Take(wordsPerChunk));
    }
}
