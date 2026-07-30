using System.Speech.Recognition;

namespace SkillMyScreen;

public static class LocalSpeechTranscriber
{
    public static async Task<string?> TryTranscribeAsync(byte[] wav, CancellationToken cancellationToken = default)
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

    private static string? Transcribe(byte[] wav)
    {
        using var stream = new MemoryStream(wav, writable: false);
        using var engine = new SpeechRecognitionEngine();
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToWaveStream(stream);
        var segments = new List<string>();
        RecognitionResult? result;
        while ((result = engine.Recognize()) is not null)
        {
            var text = result.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) segments.Add(text);
        }
        return segments.Count == 0 ? null : string.Join(" ", segments);
    }
}

public static class TranscriptSegmenter
{
    public static IReadOnlyList<TranscriptSegment> Split(string text, long durationMilliseconds, double confidence, string source = "provider")
    {
        var sentences = text.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sentences.Length == 0) return [];
        var duration = Math.Max(1000, durationMilliseconds);
        var slice = Math.Max(1000, duration / sentences.Length);
        return sentences.Select((sentence, index) =>
            new TranscriptSegment(Math.Min(duration, index * slice), Math.Min(duration, (index + 1) * slice), sentence, confidence)).ToArray();
    }
}
