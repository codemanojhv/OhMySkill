using System.Text;
using System.Drawing;
using System.Drawing.Imaging;

namespace OhMySkill;

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
            var evidenceTrace = new RecordingTrace { Title = "Evidence test", HasAudio = true };
            var actionId = Guid.NewGuid();
            var evidenceFrame = new FrameEvidence(100, "before action", [1, 2, 3], "before-1", FrameRole.Before, actionId);
            var narratedSegment = new TranscriptSegment(0, 500, "Open the report", 0.9, "provider", "provider-segment", ["before-1", "after-1"]);
            evidenceTrace.Transcript.Add(narratedSegment);
            evidenceTrace.Actions.Add(new ActionEvidence(actionId, 1, 100, 400, TraceEventKind.Click, "left click", null, evidenceFrame, evidenceFrame with { Id = "after-1", Role = FrameRole.After, ElapsedMilliseconds = 400 }, [narratedSegment]));
            evidenceTrace.Interpretations.Add(ActionUnderstandingFactory.FromAction(evidenceTrace.Actions[0]));
            var evidencePrompt = CompilerPrompt.Build(evidenceTrace, [evidenceFrame], audioAttached: true);
            Assert(evidencePrompt.Contains("Paired action evidence", StringComparison.Ordinal), "paired action prompt context");
            Assert(evidencePrompt.Contains("Open the report", StringComparison.Ordinal), "nearby narration prompt context");
            Assert(evidencePrompt.Contains("Action-level multimodal interpretations", StringComparison.Ordinal), "action interpretation synthesis context");
            Assert(evidencePrompt.Contains("imageRefs=before-1,after-1", StringComparison.Ordinal), "timestamped image references in compiler prompt");
            Assert(ActionPrompt.Build(evidenceTrace.Actions, [evidenceFrame, evidenceFrame with { Id = "after-1", Role = FrameRole.After, ElapsedMilliseconds = 400 }], false).Contains("imageRefs=before-1,after-1", StringComparison.Ordinal), "timestamped image references in action prompt");
            Assert(ActionPrompt.Build(evidenceTrace.Actions, [evidenceFrame], false).Contains(actionId.ToString(), StringComparison.Ordinal), "action batch evidence mapping");
            var providerHandler = new FakeProviderHandler(actionId);
            using (var providerHttp = new HttpClient(providerHandler))
            {
                var provider = new AiProviderService(providerHttp);
                var providerSettings = new AiSettings { Provider = "OpenAI", Endpoint = "https://fake.local/v1", Model = "test-model", UseAi = true, EncryptedApiKey = SecretBox.Protect("test-key") };
                var providerDraft = provider.GenerateDraftAsync(evidenceTrace, providerSettings, [evidenceFrame, evidenceFrame with { Id = "after-1", Role = FrameRole.After, ElapsedMilliseconds = 400 }], [new AudioWindowEvidence(0, 1000, new byte[64])]).GetAwaiter().GetResult();
                Assert(providerDraft is not null, "AI provider draft parsing");
                Assert(providerHandler.SawImage && providerHandler.SawAudio, "AI provider rich evidence payload");
            }
            var passwordTarget = new UiTarget("app", "window", "Password", null, "Edit", null, null, [], 0, 0, 10, 10, true, true);
            var protectedAction = evidenceTrace.Actions[0] with { Kind = TraceEventKind.TextEntry, Target = passwordTarget, Detail = "text entered in protected field" };
            Assert(!ActionUnderstandingFactory.FromAction(protectedAction).Instruction.Contains("Password", StringComparison.Ordinal), "protected text action");
            var estimatedSegments = TranscriptSegmenter.Split("Open the report. Verify the result.", 2000, 0.9);
            Assert(estimatedSegments.Count == 2, "transcript segmentation");
            Assert(estimatedSegments.All(segment => segment.Timing == "estimated-window" && segment.EndMilliseconds > segment.StartMilliseconds), "estimated transcript timing");
            var unchanged = TestPng(Color.White);
            Assert(ScreenCapture.IsVisuallyStable(unchanged, unchanged), "visual settle detection");
            Assert(!ScreenCapture.IsVisuallyStable(unchanged, TestPng(Color.Black)), "visual change detection");
            Assert(LocalSpeechTranscriber.TryTranscribeAsync([]).GetAwaiter().GetResult() is null, "empty audio handling");
            var prompt = PromptBuilder.Build(draft, @"E:\OhMySkill\Documents\SKILL.md");
            Assert(prompt.Contains("/mnt/e/OhMySkill/Documents/SKILL.md", StringComparison.Ordinal), "WSL prompt path");
            var outputRoot = Path.Combine(AppContext.BaseDirectory, "self-check-skills");
            draft.Procedure.Add(new SkillStep(1, "Open the reviewed report.", "report", "The report is visible."));
            var saved = SkillStorage.Save(draft, outputRoot);
            Assert(File.Exists(saved) && File.ReadAllText(saved).Contains("## Procedure", StringComparison.Ordinal), "SKILL.md save");
            Directory.Delete(outputRoot, true);
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var clear = Encoding.UTF8.GetBytes("private test payload");
            var path = Path.Combine(AppPaths.Root, "self-check", "payload.enc");
            EncryptedFile.Write(path, clear, key);
            Assert(clear.SequenceEqual(EncryptedFile.Read(path, key)), "AES-GCM roundtrip");
            Directory.Delete(Path.Combine(AppPaths.Root, "self-check"), true);
            Console.Error.WriteLine("Oh My Skill self-check passed.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Oh My Skill self-check failed: " + ex.Message);
            return false;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private static byte[] TestPng(Color color)
    {
        using var bitmap = new Bitmap(32, 18);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private sealed class FakeProviderHandler(Guid actionId) : HttpMessageHandler
    {
        public bool SawImage { get; private set; }
        public bool SawAudio { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            SawImage |= body.Contains("data:image/png;base64", StringComparison.Ordinal);
            SawAudio |= body.Contains("input_audio", StringComparison.Ordinal);
            var content = body.Contains("Interpret each demonstrated", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { actions = new[] { new { actionId, order = 1, includeInSkill = true, userIntent = "Open the report", instruction = "Open the report.", visibleBefore = "The report is visible.", observedChange = "The report opens.", expectedResult = "The report is open.", possibleMistake = (string?)null, confidence = 0.9, uncertainty = (string?)null } } })
                : JsonSerializer.Serialize(new SkillDraft
                {
                    Name = "evidence-test",
                    Title = "Evidence test",
                    Description = "Open the report when the user asks to review it.",
                    Intent = "Review the report.",
                    Goal = "Open the report and verify it is visible.",
                    Preconditions = ["The report is available."],
                    Procedure = [new SkillStep(1, "Open the report.", "report", "The report is visible.")],
                    Safety = ["Ask before sending or publishing."],
                    Verification = ["Confirm the report is visible."],
                    Recovery = ["If the report is missing, ask the user."]
                });
            var envelope = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
        }
    }
}
