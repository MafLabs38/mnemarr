using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Notes;

public sealed class NoteRightsFixture
{
    [Theory]
    [InlineData(SourceKind.Knowledge, SourceMode.ReadWrite, true, true, NoteRightsReason.Writable)]
    [InlineData(SourceKind.Memory, SourceMode.ReadWrite, true, true, NoteRightsReason.Writable)]
    [InlineData(SourceKind.Knowledge, SourceMode.ReadOnly, true, false, NoteRightsReason.SourceReadOnly)]
    [InlineData(SourceKind.Memory, SourceMode.ReadOnly, true, false, NoteRightsReason.SourceReadOnly)]
    [InlineData(SourceKind.ApplicationDocs, SourceMode.ReadWrite, true, false, NoteRightsReason.KindNotWritable)]
    [InlineData(SourceKind.ApplicationDocs, SourceMode.ReadOnly, true, false, NoteRightsReason.KindNotWritable)]
    [InlineData(SourceKind.Knowledge, SourceMode.ReadWrite, false, false, NoteRightsReason.NoPermission)]
    [InlineData(SourceKind.ApplicationDocs, SourceMode.ReadOnly, false, false, NoteRightsReason.NoPermission)]
    public void should_say_what_a_caller_may_do_and_why_with_the_permission_first_then_the_kind_then_the_switch(SourceKind kind, SourceMode mode, bool permitted, bool canWrite, string reason)
    {
        NoteRights.Evaluate(kind, mode, permitted).Should().Be(new NoteRights(canWrite, reason));
    }

    [Fact]
    public void should_have_words_for_every_refusal_and_none_for_a_right()
    {
        NoteRights.Evaluate(SourceKind.Knowledge, SourceMode.ReadOnly, true).Refusal.Should().Contain("read-only");
        NoteRights.Evaluate(SourceKind.ApplicationDocs, SourceMode.ReadWrite, true).Refusal.Should().Contain("repository");
        NoteRights.Evaluate(SourceKind.Knowledge, SourceMode.ReadWrite, false).Refusal.Should().Contain("role");
        NoteRights.Evaluate(SourceKind.Knowledge, SourceMode.ReadWrite, true).Refusal.Should().BeEmpty();
    }
}
