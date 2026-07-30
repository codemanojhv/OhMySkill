using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;

namespace SkillMyScreen;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkillMyScreen");
    public static string Sessions => Path.Combine(Root, "sessions");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Skills => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SkillMyScreen", "skills");
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class SecretBox
{
    public static string Protect(string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        var protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedData);
    }

    public static string Unprotect(string value)
    {
        var data = Convert.FromBase64String(value);
        var clear = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clear);
    }
}

public static class EncryptedFile
{
    public static void Write(string path, ReadOnlySpan<byte> clear, ReadOnlySpan<byte> key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, clear, cipher, tag);
        using var stream = File.Create(path);
        stream.Write(nonce);
        stream.Write(tag);
        stream.Write(cipher);
    }

    public static byte[] Read(string path, ReadOnlySpan<byte> key)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 28) throw new InvalidDataException("Encrypted artifact is incomplete.");
        var nonce = bytes.AsSpan(0, 12);
        var tag = bytes.AsSpan(12, 16);
        var cipher = bytes.AsSpan(28);
        var clear = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, clear);
        return clear;
    }
}

public sealed class SecureSessionStore
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public string Folder { get; }

    public SecureSessionStore(Guid id)
    {
        Folder = Path.Combine(AppPaths.Sessions, id.ToString("N"));
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "key.dpapi"), SecretBox.Protect(Convert.ToBase64String(_key)), Encoding.UTF8);
    }

    public void WriteTrace(RecordingTrace trace)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(trace, JsonDefaults.Options);
        EncryptedFile.Write(Path.Combine(Folder, "trace.json.enc"), data, _key);
    }

    public void WriteAudio(byte[] wav)
    {
        if (wav.Length == 0) return;
        EncryptedFile.Write(Path.Combine(Folder, "audio.wav.enc"), wav, _key);
    }

    public string WriteAudioChunk(string name, byte[] wav)
    {
        var relative = Path.Combine("audio", name + ".wav.enc");
        EncryptedFile.Write(Path.Combine(Folder, relative), wav, _key);
        return relative.Replace('\\', '/');
    }

    public byte[] ReadAudioChunk(string relativePath)
    {
        return EncryptedFile.Read(Path.Combine(Folder, relativePath.Replace('/', Path.DirectorySeparatorChar)), _key);
    }

    public void WriteFrame(string name, byte[] png)
    {
        EncryptedFile.Write(Path.Combine(Folder, "frames", name + ".enc"), png, _key);
    }

    public byte[] ReadFrame(string name)
    {
        return EncryptedFile.Read(Path.Combine(Folder, "frames", name + ".enc"), _key);
    }

    public void DeleteAfterSave()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
    }
}

public static class SettingsStore
{
    public static AiSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.Settings)) return new AiSettings();
            return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(AppPaths.Settings), JsonDefaults.Options) ?? new AiSettings();
        }
        catch { return new AiSettings(); }
    }

    public static void Save(AiSettings settings, string? clearApiKey)
    {
        Directory.CreateDirectory(AppPaths.Root);
        settings.EncryptedApiKey = string.IsNullOrWhiteSpace(clearApiKey) ? settings.EncryptedApiKey : SecretBox.Protect(clearApiKey);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(settings, JsonDefaults.Options), Encoding.UTF8);
    }

    public static string? GetApiKey(AiSettings settings)
    {
        try { return string.IsNullOrWhiteSpace(settings.EncryptedApiKey) ? null : SecretBox.Unprotect(settings.EncryptedApiKey); }
        catch { return null; }
    }
}

public static class SkillName
{
    public static string Slugify(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "computer-workflow" : slug[..Math.Min(slug.Length, 60)].Trim('-');
    }
}

public static class SkillRenderer
{
    public static string Render(SkillDraft draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {Yaml(draft.Name)}");
        sb.AppendLine($"description: {Yaml(draft.Description)}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {draft.Title.Trim()}");
        Section(sb, "Intent", [draft.Intent]);
        Section(sb, "Goal", [draft.Goal]);
        Section(sb, "Required inputs", draft.Inputs.Count == 0 ? ["- No explicit runtime inputs were identified."] : draft.Inputs.Select(i => $"- `{i.Name}`: {i.Description}{(i.Secret ? " (secret; never print or store the value)" : "")}"));
        Section(sb, "Preconditions", draft.Preconditions);
        Section(sb, "Procedure", draft.Procedure.Count == 0 ? ["1. Ask the user to describe the missing procedure."] : draft.Procedure.Select(s => $"{s.Order}. {s.Instruction} Verify: {s.ExpectedResult}"));
        Section(sb, "Decision rules", draft.DecisionRules);
        Section(sb, "Safety", draft.Safety);
        Section(sb, "Verification", draft.Verification);
        Section(sb, "Recovery", draft.Recovery);
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Yaml(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        sb.AppendLine();
        sb.AppendLine($"## {title}");
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l))) sb.AppendLine(line.Trim());
    }
}

public static class PromptBuilder
{
    public static string Build(SkillDraft draft, string skillPath)
    {
        var windowsPath = Path.GetFullPath(skillPath);
        var prompt = new StringBuilder();
        prompt.AppendLine("Read and follow the skill instructions in:");
        prompt.AppendLine();
        prompt.AppendLine($"\"{windowsPath}\"");
        prompt.AppendLine();
        prompt.AppendLine("Use that file as the procedural source of truth. Read it before acting and follow its prerequisites, required inputs, safety rules, recovery behavior, and verification steps.");
        prompt.AppendLine();
        prompt.AppendLine("Task inputs:");
        foreach (var input in draft.Inputs) prompt.AppendLine($"- {input.Name}: ask me if not provided");
        prompt.AppendLine();
        prompt.AppendLine("Do not guess between ambiguous targets. Ask before any external, destructive, publishing, sending, purchasing, or submission action.");
        prompt.AppendLine("If you do not have the required browser, shell, file, or computer-control capability, say so clearly and do not pretend the task was completed.");
        prompt.AppendLine("Report the final verification result.");
        if (windowsPath.Length >= 3 && windowsPath[1] == ':')
        {
            var wsl = "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
            prompt.AppendLine();
            prompt.AppendLine($"WSL path if using WSL: \"{wsl}\"");
        }
        return prompt.ToString();
    }
}

public static class SkillStorage
{
    public static string Save(SkillDraft draft, string root)
    {
        SkillDraftValidator.Validate(draft);
        Directory.CreateDirectory(root);
        var slug = SkillName.Slugify(draft.Name);
        var folder = Path.Combine(root, slug);
        var index = 2;
        while (Directory.Exists(folder)) folder = Path.Combine(root, $"{slug}-{index++}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "SKILL.md");
        var temp = path + ".tmp";
        File.WriteAllText(temp, SkillRenderer.Render(draft), new UTF8Encoding(false));
        File.Move(temp, path);
        return path;
    }
}
