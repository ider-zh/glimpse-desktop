using Glimpse.Contracts;
using Glimpse.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glimpse.Tests;

public class AgentStorageTests
{
    [Fact]
    public async Task VersionOneDatabaseMigratesWithoutLosingReaderDataAndDeletesTurnsWithSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-migration-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sessions.db");
        try
        {
            var original = new SessionRepository(path);
            var session = new SessionData(Guid.NewGuid(), "Existing reader session", "paused", 1, 2,
                new(-400, 50, 600, 400), 7, [], [], []);
            await original.SaveAsync(session);
            using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                legacy.Open(); using var command = legacy.CreateCommand();
                // Version 1 has precisely the Reader tables and no agent_turns table.
                command.CommandText = "DROP TABLE agent_turns; PRAGMA user_version=1";
                command.ExecuteNonQuery();
            }
            var migrated = new SessionRepository(path);
            var restored = await migrated.LoadAsync(session.Id);
            Assert.Equal(session.Revision, restored!.Revision); Assert.Equal(session.Rect, restored.Rect);
            var result = new AgentResult(Guid.NewGuid(), session.Id, 7, "offline", "summary", "summary.v1",
                "completed", [new("Summary", "Synthetic result only")], null, null, 12, []);
            await migrated.SaveAgentResultAsync(result, "offline-model");
            await migrated.SaveAgentResultAsync(result, "offline-model");
            var turns = await new SessionRepository(path).ListAgentResultsAsync(session.Id);
            Assert.Single(turns); Assert.Equal(result.PreviewId, turns[0].PreviewId);
            Assert.Equal(result.Cards[0].Text, turns[0].Cards[0].Text);
            await migrated.DeleteAsync(session.Id);
            Assert.Empty(await migrated.ListAgentResultsAsync(session.Id));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
