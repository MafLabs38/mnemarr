using Mnemarr.Core.Notes;

namespace Mnemarr.Core.Test.Notes;

public sealed class LinkRetargeterFixture
{
    [Fact]
    public void should_keep_the_style_a_wikilink_was_written_in_and_its_heading_and_its_text()
    {
        var text = "[[docker]] [[docker|the tool]] [[devops/docker#Images]] ![[docker]] [[./devops/docker]] [[Other]]";

        var (result, changed) = LinkRetargeter.Retarget(text, "index.md", "devops/docker.md", "devops/containers/engine.md");

        changed.Should().Be(5);
        result.Should().Be("[[engine]] [[engine|the tool]] [[devops/containers/engine#Images]] ![[engine]] [[./devops/containers/engine]] [[Other]]");
    }

    [Fact]
    public void should_write_a_relative_link_from_the_folder_of_the_note_that_holds_it()
    {
        var (result, changed) = LinkRetargeter.Retarget("[[../devops/docker]] and [[./docker]]", "devops/index.md", "devops/docker.md", "ops/docker.md");

        changed.Should().Be(2, "both lead to the moved note, from the folder of the note that holds them");
        result.Should().Be("[[../ops/docker]] and [[../ops/docker]]");
    }

    [Fact]
    public void should_rewrite_ordinary_links_to_the_note_and_keep_their_heading_and_title()
    {
        var text = "[a](../devops/docker.md) [b](../devops/docker.md#images) [c](../devops/docker.md \"Docker\") [d](other.md) [e](https://example.com/docker.md)";

        var (result, changed) = LinkRetargeter.Retarget(text, "notes/index.md", "devops/docker.md", "ops/my docker.md");

        changed.Should().Be(3);
        result.Should().Be("[a](../ops/my%20docker.md) [b](../ops/my%20docker.md#images) [c](../ops/my%20docker.md \"Docker\") [d](other.md) [e](https://example.com/docker.md)");
    }

    [Fact]
    public void should_leave_code_and_images_and_other_notes_alone()
    {
        var text = "`[[docker]]`\n```\n[[docker]]\n```\n![[docker.png]] [[docker-compose]] [[Docker Swarm]]";

        var (result, changed) = LinkRetargeter.Retarget(text, "index.md", "devops/docker.md", "ops/engine.md");

        changed.Should().Be(0);
        result.Should().Be(text);
    }

    [Fact]
    public void should_keep_the_extension_a_link_was_written_with_and_the_line_endings_of_the_note()
    {
        var (result, changed) = LinkRetargeter.Retarget("# T\r\nSee [[docker.md]].\r\n", "index.md", "docker.md", "engine.md");

        changed.Should().Be(1);
        result.Should().Be("# T\r\nSee [[engine.md]].\r\n");
    }

    [Fact]
    public void should_not_touch_the_front_matter_and_keep_a_byte_order_mark()
    {
        var text = "﻿---\ntags: [docker]\n---\n[[docker]]";

        var (result, changed) = LinkRetargeter.Retarget(text, "index.md", "docker.md", "engine.md");

        changed.Should().Be(1);
        result.Should().Be("﻿---\ntags: [docker]\n---\n[[engine]]");
    }

    [Fact]
    public void should_rebase_the_relative_links_of_a_note_that_changes_folder_so_that_they_lead_to_the_same_files()
    {
        var text = "[[./sibling]] [[../index]] [[root name]] [a](sibling.md) [b](../index.md#top) [c](https://example.com/x.md) ![i](img/a.png)";

        var (result, changed) = LinkRetargeter.Rebase(text, "devops/docker.md", "ops/deep/docker.md");

        changed.Should().Be(5);
        result.Should().Be("[[../../devops/sibling]] [[../../index]] [[root name]] [a](../../devops/sibling.md) [b](../../index.md#top) [c](https://example.com/x.md) ![i](../../devops/img/a.png)");
    }

    [Fact]
    public void should_leave_a_note_that_stays_in_its_folder_as_it_is()
    {
        var (result, changed) = LinkRetargeter.Rebase("[a](sibling.md)", "devops/docker.md", "devops/engine.md");

        changed.Should().Be(0);
        result.Should().Be("[a](sibling.md)");
    }
}
