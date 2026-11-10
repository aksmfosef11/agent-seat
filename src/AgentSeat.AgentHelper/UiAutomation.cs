using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSeat.Core.Agent;

namespace AgentSeat.AgentHelper;

/// <summary>Owns references for this helper and bounds the lifetime of every UIA call.</summary>
internal sealed class UiAutomation(string? workerPath = null)
{
    private const int TimeoutMilliseconds = 3500;
    private const int MaxTargets = 4096;
    private static readonly TimeSpan RefLifetime = TimeSpan.FromMinutes(2);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, (UiIdentity Identity, long Seen)> _targets = new(StringComparer.Ordinal);

    internal AgentResponse Run(AgentRequest request)
    {
        var now = Stopwatch.GetTimestamp();
        var reference = request.Action == AgentActions.Observe ? request.RootRef : request.Ref;
        UiIdentity? target = null;
        if (reference is not null)
        {
            if (!_targets.TryGetValue(reference, out var entry) || Stopwatch.GetElapsedTime(entry.Seen, now) > RefLifetime)
            {
                _targets.Remove(reference);
                return UiObservation.Refusal("stale_ref", "This element ref expired or belongs to another helper. Observe again.");
            }
            target = entry.Identity;
        }

        var result = RunWorker(request, target);
        if (request.Action == AgentActions.Observe && request.RootRef is null && !result.Response.Ok)
        {
            return AgentResponse.Success(new JsonObject
            {
                ["available"] = false,
                ["text"] = result.Response.Error + " Use an image."
            });
        }
        if (result.Response.Data is not { } data || result.Targets is not { Count: > 0 } targets)
        {
            return result.Response;
        }

        var published = new JsonArray();
        foreach (var item in targets)
        {
            // Stable while the element identity is unchanged, so unchanged observations can still be omitted.
            var digest = HMACSHA256.HashData(_key, JsonSerializer.SerializeToUtf8Bytes(item.Identity, AgentJson.Options));
            var id = "r-" + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
            _targets[id] = (item.Identity, now);
            published.Add(new JsonObject
            {
                ["index"] = item.Index, ["ref"] = id, ["canExpand"] = item.CanExpand,
                ["actions"] = JsonSerializer.SerializeToNode(item.Actions, AgentJson.Options)
            });
        }
        data["targets"] = published;
        if (_targets.Count > MaxTargets)
        {
            foreach (var expired in _targets.OrderBy(entry => entry.Value.Seen)
                         .Take(_targets.Count - MaxTargets).Select(entry => entry.Key).ToArray())
            {
                _targets.Remove(expired);
            }
        }
        return result.Response;
    }

    private UiWorkerResult RunWorker(AgentRequest request, UiIdentity? target)
    {
        var mutation = AgentActions.IsInput(request.Action);
        var started = false;
        var mayHaveDispatched = false;
        using var worker = new Process
        {
            StartInfo = new ProcessStartInfo(workerPath ?? Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8
            }
        };
        worker.StartInfo.ArgumentList.Add("--observe-uia");
        try
        {
            // A worker cannot read a mutation until it is tied to the parent's lifetime.
            using var job = mutation ? UiWorkerJob.Create() : null;
            started = worker.Start();
            if (job is not null) UiWorkerJob.Add(job, worker);
            var output = worker.StandardOutput.ReadToEndAsync();
            mayHaveDispatched = mutation;
            worker.StandardInput.WriteLine(JsonSerializer.Serialize(new UiWorkerRequest(request, target), AgentJson.Options));
            worker.StandardInput.Close();
            if (!worker.WaitForExit(TimeoutMilliseconds))
            {
                return Failed("uia_timeout", "UI Automation timed out.", mayHaveDispatched);
            }
            var json = output.GetAwaiter().GetResult();
            return json.Length <= 250000 && JsonSerializer.Deserialize<UiWorkerResult>(json, AgentJson.Options) is { } result
                ? result : Failed("uia_unavailable", "UI Automation did not return a usable result.", mayHaveDispatched);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or JsonException)
        {
            return Failed("uia_unavailable", "UI Automation is unavailable.", mayHaveDispatched);
        }
        finally
        {
            // Covers timeouts and setup failures, including a worker waiting for its first request.
            if (started)
            {
                try
                {
                    if (!worker.HasExited) worker.Kill();
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    private static UiWorkerResult Failed(string code, string message, bool mayHaveDispatched) =>
        new(AgentResponse.Failure(code, message) with
        {
            Disposition = mayHaveDispatched ? AgentDisposition.Uncertain : AgentDisposition.NotDelivered
        });
}
