using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Chat;

/// <summary>Who owns a conversation of the chat.</summary>
public sealed record ChatOwner(Guid AccountId, string OwnerName);

/// <summary>The person and the conversation behind a token that the runner presents to the MCP server, with what the person may do now.</summary>
public sealed record ChatTokenIdentity(AccessPrincipal Principal, string ConversationId);

/// <summary>
/// The rules of the chat that Mnemarr keeps (ADR 0035): a conversation belongs to the person who started it (the runner keeps what was said; Mnemarr keeps whose it is), and the
/// runner reaches the MCP server with a token tied to a person and a conversation. A token lasts 30 minutes from the last message of its conversation and 12 hours at most, opens
/// only the MCP server, and has no permission of its own: what it may do is what the person's role allows <b>now</b>, read at every call, so that a person whose role was
/// taken away, or whose account was disabled, loses the chat's hands at once. Only the hash of a token is kept.
/// </summary>
public sealed class ChatAccess
{
    public const string TokenPrefix = "mnc_";
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan TokenMaxLifetime = TimeSpan.FromHours(12);

    private readonly IDatabase _database;
    private readonly IAccountStore _accounts;
    private readonly TimeProvider _clock;

    public ChatAccess(IDatabase database, IAccountStore accounts, TimeProvider clock)
    {
        _database = database;
        _accounts = accounts;
        _clock = clock;
    }

    public void Register(string conversationId, Account owner)
    {
        using var connection = _database.Open();
        Execute(connection, "INSERT OR REPLACE INTO chat_conversations (id, account_id, owner_name, created_at) VALUES ($id, $account, $name, $at)", ("$id", conversationId), ("$account", owner.Id.ToString()), ("$name", owner.Name), ("$at", _clock.GetUtcNow().ToString("O")));
    }

    /// <summary>Whether the person owns the conversation: the answer for another person's conversation is "no", never "forbidden", so that its existence is not revealed.</summary>
    public bool CanAccess(string conversationId, Guid accountId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM chat_conversations WHERE id = $id AND account_id = $account";
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$account", accountId.ToString());
        return command.ExecuteScalar() is not null;
    }

    /// <summary>The conversations that someone owns, by id: what the list of the runner is narrowed with.</summary>
    public IReadOnlyDictionary<string, ChatOwner> Owners()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, account_id, owner_name FROM chat_conversations";
        using var reader = command.ExecuteReader();
        var owners = new Dictionary<string, ChatOwner>(StringComparer.Ordinal);
        while (reader.Read())
        {
            owners[reader.GetString(0)] = new ChatOwner(Guid.Parse(reader.GetString(1)), reader.GetString(2));
        }

        return owners;
    }

    /// <summary>Forgets a conversation and every token of it.</summary>
    public void Forget(string conversationId)
    {
        using var connection = _database.Open();
        Execute(connection, "DELETE FROM chat_conversations WHERE id = $id", ("$id", conversationId));
        Execute(connection, "DELETE FROM chat_tokens WHERE conversation_id = $id", ("$id", conversationId));
    }

    /// <summary>The token the runner uses as the MCP key for this person's turns in this conversation (shown once, kept as a hash).</summary>
    public string Mint(Guid accountId, string conversationId)
    {
        var now = _clock.GetUtcNow();
        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        using var connection = _database.Open();
        Execute(connection, "DELETE FROM chat_tokens WHERE absolute_expires_at < $now OR expires_at < $now", ("$now", now.ToString("O")));
        // A message keeps the earlier tokens of its conversation alive too: a process that the runner started already holds one of them.
        Execute(connection, "UPDATE chat_tokens SET expires_at = $expires WHERE conversation_id = $conversation AND absolute_expires_at > $expires", ("$expires", (now + TokenLifetime).ToString("O")), ("$conversation", conversationId));
        Execute(
            connection,
            "INSERT INTO chat_tokens (token_hash, account_id, conversation_id, expires_at, absolute_expires_at) VALUES ($hash, $account, $conversation, $expires, $absolute)",
            ("$hash", SecretHash.Of(token)),
            ("$account", accountId.ToString()),
            ("$conversation", conversationId),
            ("$expires", (now + TokenLifetime).ToString("O")),
            ("$absolute", (now + TokenMaxLifetime).ToString("O")));
        return token;
    }

    /// <summary>The person and the conversation behind a token, with the person's permissions as they are now; null when the token is unknown or expired, or the person is disabled or gone.</summary>
    public ChatTokenIdentity? Authenticate(string token)
    {
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id, conversation_id, expires_at, absolute_expires_at FROM chat_tokens WHERE token_hash = $hash";
        command.Parameters.AddWithValue("$hash", SecretHash.Of(token));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture) <= now || DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture) <= now)
        {
            return null;
        }

        var account = _accounts.Get(Guid.Parse(reader.GetString(0)));
        // Nothing the chat's hands hold goes beyond what the person's role gives today, and the chat is for people: a person who may not use the chat any more has no hands in it.
        if (account is not { Enabled: true, Kind: AccountKind.User } || !account.Permissions.Contains(Permissions.ChatUse))
        {
            return null;
        }

        return new ChatTokenIdentity(new AccessPrincipal(account, account.Permissions, null, null), reader.GetString(1));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
