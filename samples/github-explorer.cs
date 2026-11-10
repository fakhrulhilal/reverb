#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;
using static Reverb.Notebook;
using System.Globalization;
using System.Text.Json.Nodes;

// %% [markdown] id=intro
// # GitHub repository explorer
//
// Type a repository as `owner/name`. This notebook calls GitHub's public REST API without a token,
// which GitHub allows about 60 times an hour.
//
// It reads the JSON with `JsonNode`. File-based apps turn off reflection-based JSON
// serialization, so `GetFromJsonAsync<T>` would need a source-generated context.

// %% id=input
var repo = Ui.Text("Repository", value: "dotnet/runtime");
var refresh = Ui.Button("Refresh");

// %% id=client
var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
http.DefaultRequestHeaders.UserAgent.ParseAdd("reverb-sample");
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

// %% id=info
// Reading the button makes this cell run again when it's pressed.
_ = refresh.Value;
var name = repo.Value.Trim();
JsonNode? info = null;
try
{
    info = JsonNode.Parse(await http.GetStringAsync($"repos/{name}", Notebook.CancellationToken));
}
catch (HttpRequestException e)
{
    Md(e.StatusCode switch
    {
        System.Net.HttpStatusCode.NotFound => $"There's no public repository called **{name}**. Check the spelling: it's `owner/name`.",
        System.Net.HttpStatusCode.Forbidden => "GitHub says no: probably the hourly limit for requests without a token. Try again later.",
        _ => $"GitHub didn't answer: {e.Message}",
    });
}
if (info is not null)
{
    Md($"""
        ## [{info["full_name"]}]({info["html_url"]})

        {info["description"]}

        **{(int)info["stargazers_count"]!:N0}** stars, **{(int)info["forks_count"]!:N0}** forks, **{(int)info["open_issues_count"]!:N0}** open issues and pull requests. Mostly {info["language"]}.
        """);
}

// %% id=releases
List<Release> releases = [];
if (info is not null)
{
    var json = JsonNode.Parse(await http.GetStringAsync($"repos/{name}/releases?per_page=50", Notebook.CancellationToken))!.AsArray();
    releases =
    [
        .. json.Where(r => r?["published_at"] is not null).Select(r => new Release(
            (string)r!["tag_name"]!,
            DateTimeOffset.Parse((string)r["published_at"]!, CultureInfo.InvariantCulture),
            (bool)r["prerelease"]!)),
    ];
}
releases.Take(10).Dump("Latest releases");

// %% id=cadence
Chart.Bar(
    releases.GroupBy(r => r.Published.ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(g => g.Key).Select(g => new ReleaseMonth(g.Key, g.Count())),
    x: m => m.Month, y: m => m.Releases, title: "Releases per month");

// %% types
record Release(string Tag, DateTimeOffset Published, bool Prerelease);
record ReleaseMonth(string Month, int Releases);
