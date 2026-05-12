using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class SlackEventMapperTests
{
    [Fact]
    public void MapSocketModeEvent_MapsAppMentionAndStripsBotMention()
    {
        var message = SlackEventMapper.MapSocketModeEvent(
            """
            {
              "type": "events_api",
              "event": {
                "type": "app_mention",
                "channel": "C123OPS",
                "user": "U123",
                "ts": "1778351400.123456",
                "thread_ts": "1778351000.000001",
                "text": "<@UBOT> inspect deployment",
                "files": [
                  {
                    "name": "report.txt",
                    "url_private_download": "https://slack.example/files/report.txt"
                  }
                ]
              }
            }
            """,
            botUserId: "UBOT",
            users: new Dictionary<string, SlackUserSnapshot>
            {
                ["U123"] = new("U123", "alice", "Alice Ops")
            },
            provider: "openai",
            model: "gpt-5.4");

        Assert.NotNull(message);
        Assert.Equal("C123OPS", message.ChannelId);
        Assert.Equal("inspect deployment", message.Text);
        Assert.Equal("1778351400.123456", message.Ts);
        Assert.Equal("1778351000.000001", message.ThreadTs);
        Assert.Equal("U123", message.User);
        Assert.Equal("alice", message.UserName);
        Assert.Equal("Alice Ops", message.DisplayName);
        Assert.Equal("openai", message.Provider);
        Assert.Equal("gpt-5.4", message.Model);
        Assert.NotNull(message.Attachments);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("report.txt", attachment.Original);
        Assert.Equal("https://slack.example/files/report.txt", attachment.Url);
        Assert.NotNull(message.Metadata);
        Assert.Equal("slack", message.Metadata["source"]);
        Assert.Equal("mention", message.Metadata["slackEventType"]);
        Assert.Equal("1", message.Metadata["attachmentCount"]);
    }

    [Fact]
    public void MapSocketModeEvent_SkipsAppMentionInDm()
    {
        var message = SlackEventMapper.MapSocketModeEvent(
            """
            {
              "event": {
                "type": "app_mention",
                "channel": "D123DM",
                "user": "U123",
                "ts": "1778351400.123456",
                "text": "<@UBOT> hello"
              }
            }
            """,
            botUserId: "UBOT");

        Assert.Null(message);
    }

    [Fact]
    public void MapSocketModeEvent_MapsDirectMessage()
    {
        var message = SlackEventMapper.MapSocketModeEvent(
            """
            {
              "event": {
                "type": "message",
                "channel": "D123DM",
                "channel_type": "im",
                "user": "U123",
                "ts": "1778351400.123456",
                "text": "hello mom"
              }
            }
            """,
            botUserId: "UBOT");

        Assert.NotNull(message);
        Assert.Equal("D123DM", message.ChannelId);
        Assert.Equal("hello mom", message.Text);
        Assert.NotNull(message.Metadata);
        Assert.Equal("dm", message.Metadata["slackEventType"]);
    }

    [Theory]
    [InlineData("{ \"event\": { \"type\": \"message\", \"channel\": \"D123\", \"channel_type\": \"im\", \"user\": \"UBOT\", \"ts\": \"1.0\", \"text\": \"self\" } }")]
    [InlineData("{ \"event\": { \"type\": \"message\", \"channel\": \"D123\", \"channel_type\": \"im\", \"bot_id\": \"B1\", \"user\": \"U123\", \"ts\": \"1.0\", \"text\": \"bot\" } }")]
    [InlineData("{ \"event\": { \"type\": \"message\", \"channel\": \"D123\", \"channel_type\": \"im\", \"subtype\": \"message_changed\", \"user\": \"U123\", \"ts\": \"1.0\", \"text\": \"edit\" } }")]
    [InlineData("{ \"event\": { \"type\": \"message\", \"channel\": \"C123\", \"channel_type\": \"channel\", \"user\": \"U123\", \"ts\": \"1.0\", \"text\": \"channel chatter\" } }")]
    public void MapSocketModeEvent_SkipsNonProcessableMessages(string json)
    {
        var message = SlackEventMapper.MapSocketModeEvent(json, botUserId: "UBOT");

        Assert.Null(message);
    }

    [Fact]
    public void MapSocketModeEvent_AllowsDmFileShareWithoutText()
    {
        var message = SlackEventMapper.MapSocketModeEvent(
            """
            {
              "event": {
                "type": "message",
                "channel": "D123DM",
                "channel_type": "im",
                "subtype": "file_share",
                "user": "U123",
                "ts": "1778351400.123456",
                "files": [
                  {
                    "title": "screenshot.png",
                    "url_private": "https://slack.example/files/screenshot.png"
                  }
                ]
              }
            }
            """,
            botUserId: "UBOT");

        Assert.NotNull(message);
        Assert.Equal(string.Empty, message.Text);
        Assert.NotNull(message.Attachments);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("screenshot.png", attachment.Original);
        Assert.Equal("https://slack.example/files/screenshot.png", attachment.Url);
    }
}
