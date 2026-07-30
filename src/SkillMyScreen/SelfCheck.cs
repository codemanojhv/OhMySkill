using System.Text;

namespace SkillMyScreen;

public static class SelfCheck
{
    public static bool Run()
    {
        try
        {
            var slug = SkillName.Slugify("Upload Weekly Report!");
            Assert(slug == "upload-weekly-report", "slug generation");
            var draft = DraftFactory.FromTrace(new RecordingTrace { Title = "Upload Weekly Report", HasAudio = false });
            var markdown = SkillRenderer.Render(draft);
            Assert(markdown.StartsWith("---\nname: \"upload-weekly-report\""), "frontmatter rendering");
            Assert(markdown.Contains("## Intent", StringComparison.Ordinal), "intent rendering");
            Assert(markdown.Contains("## Safety", StringComparison.Ordinal), "safety section");
            var narrated = new RecordingTrace { Title = "Upload Weekly Report", HasAudio = true };
            narrated.Transcript.Add(new TranscriptSegment(0, 1000, "Upload the reviewed report but ask before sending it.", 0.9));
            Assert(DraftFactory.FromTrace(narrated).Intent.Contains("Upload the reviewed report", StringComparison.Ordinal), "narration intent fallback");
            var contextPrompt = CompilerPrompt.Build(new RecordingTrace { Title = "Upload Weekly Report", HasAudio = true }, [new FrameEvidence(100, "marker", [1, 2, 3])], audioAttached: true);
            Assert(contextPrompt.Contains("Narration is the strongest evidence", StringComparison.Ordinal), "intent prompt guidance");
            Assert(contextPrompt.Contains("Attached frame 1", StringComparison.Ordinal), "visual evidence prompt");
            Assert(LocalSpeechTranscriber.TryTranscribeAsync([]).GetAwaiter().GetResult() is null, "empty audio handling");
            var prompt = PromptBuilder.Build(draft, @"E:\SkillMyScreen\Documents\SKILL.md");
            Assert(prompt.Contains("/mnt/e/SkillMyScreen/Documents/SKILL.md", StringComparison.Ordinal), "WSL prompt path");
            var outputRoot = Path.Combine(AppContext.BaseDirectory, "self-check-skills");
            var saved = SkillStorage.Save(draft, outputRoot);
            Assert(File.Exists(saved) && File.ReadAllText(saved).Contains("## Procedure", StringComparison.Ordinal), "SKILL.md save");
            Directory.Delete(outputRoot, true);
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var clear = Encoding.UTF8.GetBytes("private test payload");
            var path = Path.Combine(AppPaths.Root, "self-check", "payload.enc");
            EncryptedFile.Write(path, clear, key);
            Assert(clear.SequenceEqual(EncryptedFile.Read(path, key)), "AES-GCM roundtrip");
            Directory.Delete(Path.Combine(AppPaths.Root, "self-check"), true);
            Console.Error.WriteLine("SkillMyScreen self-check passed.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SkillMyScreen self-check failed: " + ex.Message);
            return false;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
