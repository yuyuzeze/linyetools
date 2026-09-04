using System.Text.Json;
using KikuCaption.ComponentManagement.Security;
using KikuCaption.ComponentManagement.Versioning;

namespace KikuCaption.ComponentManagement.Manifest;

/// <summary>
/// Strict, defensive parser for the remote manifest (R7A). Uses System.Text.Json and validates every
/// field before the app trusts it: exact schema version, known component types only, HTTPS URLs,
/// well-formed SHA-256, non-negative sizes, safe relative install paths, and numeric versions. Any
/// violation throws a <see cref="ManifestException"/> with a stable code — the parser never returns a
/// partially trusted object, and never executes anything from the document.
/// </summary>
public sealed class ManifestParser
{
    /// <summary>The only schema version this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    public RemoteManifest Parse(string json, bool allowInsecureHttp)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ManifestException("json", "Manifest is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ManifestException("json", "Manifest root must be a JSON object.");
            }

            int schema = GetInt(root, "schemaVersion", required: true)!.Value;
            if (schema != SupportedSchemaVersion)
            {
                throw new ManifestException("schema",
                    $"Unsupported manifest schemaVersion {schema} (expected {SupportedSchemaVersion}).");
            }

            string product = GetString(root, "product", required: true)!;
            string channel = GetString(root, "channel", required: true)!;

            var application = ParseApplication(root, allowInsecureHttp);
            var components = ParseComponents(root, allowInsecureHttp);

            return new RemoteManifest(schema, product, channel, application, components);
        }
    }

    private static ApplicationRelease? ParseApplication(JsonElement root, bool allowInsecureHttp)
    {
        if (!root.TryGetProperty("application", out var app) || app.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (app.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestException("application", "'application' must be an object.");
        }

        string version = GetString(app, "version", required: true)!;
        if (!ManifestVersion.TryParse(version, out _))
        {
            throw new ManifestException("version", $"Invalid application version '{version}'.");
        }

        string packageUrl = GetString(app, "packageUrl", required: true)!;
        if (!ResourceSecurity.IsAllowedUrl(packageUrl, allowInsecureHttp))
        {
            throw new ManifestException("url", "application.packageUrl must be an HTTPS URL.");
        }

        string sha = GetString(app, "sha256", required: true)!;
        if (!ResourceSecurity.IsValidSha256(sha))
        {
            throw new ManifestException("sha256", "application.sha256 is not a 64-char hex digest.");
        }

        long size = GetLong(app, "sizeBytes", required: true)!.Value;
        if (size < 0)
        {
            throw new ManifestException("size", "application.sizeBytes must be >= 0.");
        }

        string? notes = GetString(app, "releaseNotesUrl", required: false);
        if (notes is not null && !ResourceSecurity.IsAllowedUrl(notes, allowInsecureHttp))
        {
            throw new ManifestException("url", "application.releaseNotesUrl must be an HTTPS URL.");
        }

        string? minimum = GetString(app, "minimumSupportedVersion", required: false);
        if (minimum is not null && !ManifestVersion.TryParse(minimum, out _))
        {
            throw new ManifestException("version", $"Invalid minimumSupportedVersion '{minimum}'.");
        }

        return new ApplicationRelease(version, packageUrl, sha, size, notes, minimum);
    }

    private static IReadOnlyList<RemoteComponent> ParseComponents(JsonElement root, bool allowInsecureHttp)
    {
        if (!root.TryGetProperty("components", out var components) || components.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<RemoteComponent>();
        }

        if (components.ValueKind != JsonValueKind.Array)
        {
            throw new ManifestException("components", "'components' must be an array.");
        }

        var result = new List<RemoteComponent>();
        foreach (var element in components.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ManifestException("components", "Each component must be an object.");
            }

            string id = GetString(element, "id", required: true)!;

            string typeText = GetString(element, "type", required: true)!;
            if (!Enum.TryParse<RemoteComponentType>(typeText, ignoreCase: false, out var type) ||
                !Enum.IsDefined(type))
            {
                throw new ManifestException("component-type", $"Unknown component type '{typeText}'.");
            }

            string version = GetString(element, "version", required: true)!;
            if (!ManifestVersion.TryParse(version, out _))
            {
                throw new ManifestException("version", $"Invalid component version '{version}' for '{id}'.");
            }

            string url = GetString(element, "url", required: true)!;
            if (!ResourceSecurity.IsAllowedUrl(url, allowInsecureHttp))
            {
                throw new ManifestException("url", $"component '{id}' url must be an HTTPS URL.");
            }

            string sha = GetString(element, "sha256", required: true)!;
            if (!ResourceSecurity.IsValidSha256(sha))
            {
                throw new ManifestException("sha256", $"component '{id}' sha256 is not a 64-char hex digest.");
            }

            long size = GetLong(element, "sizeBytes", required: true)!.Value;
            if (size < 0)
            {
                throw new ManifestException("size", $"component '{id}' sizeBytes must be >= 0.");
            }

            string installDir = GetString(element, "installDirectory", required: true)!;
            if (!ResourceSecurity.IsSafeRelativeInstallPath(installDir))
            {
                throw new ManifestException("path",
                    $"component '{id}' installDirectory must be a safe relative path.");
            }

            var requiredFiles = ParseRequiredFiles(element, id);
            // R7A.1: each requiredFiles entry must be safe, resolve inside installDir, and be unique.
            ManifestValidator.ValidateRequiredFiles(id, installDir, requiredFiles);

            result.Add(new RemoteComponent(id, type, version, url, sha, size, installDir, requiredFiles));
        }

        // R7A.1: reject id / install-directory / cache-name conflicts across the whole component set.
        ManifestValidator.ValidateComponentCollection(result);
        return result;
    }

    private static IReadOnlyList<string> ParseRequiredFiles(JsonElement element, string id)
    {
        if (!element.TryGetProperty("requiredFiles", out var files) || files.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new ManifestException("required-files", $"component '{id}' requiredFiles must be an array.");
        }

        var list = new List<string>();
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.String)
            {
                throw new ManifestException("required-files", $"component '{id}' requiredFiles must be strings.");
            }

            // Deep validation (safe/relative/contained/unique) is done by ManifestValidator below.
            list.Add(file.GetString()!);
        }

        return list;
    }

    // ---- typed, strict property readers ----------------------------------------------------------

    private static string? GetString(JsonElement obj, string name, bool required)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new ManifestException("missing", $"Missing required field '{name}'.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ManifestException("type", $"Field '{name}' must be a string.");
        }

        var text = value.GetString();
        if (required && string.IsNullOrWhiteSpace(text))
        {
            throw new ManifestException("missing", $"Field '{name}' must not be empty.");
        }

        return text;
    }

    private static int? GetInt(JsonElement obj, string name, bool required)
    {
        var value = GetNumber(obj, name, required);
        if (value is null) return null;
        if (value.Value.TryGetInt32(out var i)) return i;
        throw new ManifestException("type", $"Field '{name}' must be a 32-bit integer.");
    }

    private static long? GetLong(JsonElement obj, string name, bool required)
    {
        var value = GetNumber(obj, name, required);
        if (value is null) return null;
        if (value.Value.TryGetInt64(out var l)) return l;
        throw new ManifestException("type", $"Field '{name}' must be a 64-bit integer.");
    }

    private static JsonElement? GetNumber(JsonElement obj, string name, bool required)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new ManifestException("missing", $"Missing required field '{name}'.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new ManifestException("type", $"Field '{name}' must be a number.");
        }

        return value;
    }
}
