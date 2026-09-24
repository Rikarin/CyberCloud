using CyberCloud.Providers.Mail.Dns;
using System.Text;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The DNS message format, fed answers built byte by byte — the shapes a real server may send
///     and CoreDNS, in <c>MailDnsResolverTests</c>, happens not to.
/// </summary>
public sealed class MailDnsWireTests {
    const ushort Id = 0x1234;
    const ushort Txt = 16;
    const ushort Mx = 15;
    const ushort Cname = 5;

    [Fact]
    public void AQueryIsOneRecursiveQuestionWithAnEdnsBuffer() {
        var query = DnsMessage.Query(Id, "cc._domainkey.example.com.", Txt, 1232);

        query[..2].ShouldBe(new byte[] { 0x12, 0x34 });
        query[2..4].ShouldBe(new byte[] { 0x01, 0x00 }, "RD set, nothing else");
        query[4..6].ShouldBe(new byte[] { 0, 1 }, "one question");
        query[10..12].ShouldBe(new byte[] { 0, 1 }, "one additional record — the OPT");

        // The name, as labels, then TXT IN.
        byte[] name = [2, (byte)'c', (byte)'c', 10, .. "_domainkey"u8.ToArray(), 7, .. "example"u8.ToArray(), 3, .. "com"u8.ToArray(), 0];
        query.AsSpan(12, name.Length).ToArray().ShouldBe(name);
        query.AsSpan(12 + name.Length, 4).ToArray().ShouldBe(new byte[] { 0, 16, 0, 1 });

        // OPT: root, type 41, class = the buffer size.
        query.AsSpan(12 + name.Length + 4, 5).ToArray().ShouldBe(new byte[] { 0, 0, 41, 0x04, 0xD0 });
    }

    [Fact]
    public void ATxtRecordOfSeveralStringsIsJoined() {
        // ⚠ THE REASON DKIM VERIFIES AT ALL. A 2048-bit key is two strings; RFC 6376 § 3.6.2.2
        // concatenates them. Split here, every correctly published key would be a mismatch.
        var first = new string('A', 255);
        var second = "BCDEF";

        var parsed = DnsMessage.Parse(Answer(Txt, Question("cc._domainkey.example.com"), Txt, TxtData(first, second)), Id, Txt);

        parsed.Answer.Resolved.ShouldBeTrue();
        parsed.Answer.Values.ShouldBe([first + second]);
    }

    [Fact]
    public void AnMxExchangeBehindACompressionPointerIsReadThroughIt() {
        // The exchange is `mx.` followed by a pointer to `example.com` in the question, which is how
        // every real server writes it.
        var question = Question("example.com");
        var exchange = new byte[] { 0, 10, 2, (byte)'m', (byte)'x', 0xC0, 12 };

        var parsed = DnsMessage.Parse(Answer(Mx, question, Mx, exchange), Id, Mx);

        parsed.Answer.Values.ShouldBe(["10 mx.example.com"]);
    }

    [Fact]
    public void OnlyRecordsOfTheAskedKindAreAnswers() {
        // A recursive resolver answering TXT for a name that is a CNAME returns the CNAME first. That
        // record is the path, not the answer.
        var question = Question("cc._domainkey.example.com");
        var cname = Record(0xC00C, Cname, Question("elsewhere.example"));
        var txt = Record(0xC00C, Txt, TxtData("v=DKIM1; p=abc"));

        var parsed = DnsMessage.Parse(Message(0x8180, question, [cname, txt]), Id, Txt);

        parsed.Answer.Values.ShouldBe(["v=DKIM1; p=abc"]);
    }

    [Fact]
    public void NxdomainIsAnAnswerThatTheNameHasNoRecord() {
        var parsed = DnsMessage.Parse(Message(0x8183, Question("nope.example.com"), []), Id, Txt);

        parsed.Answer.Resolved.ShouldBeTrue("NXDOMAIN is the server answering, and the answer is 'nothing'");
        parsed.Answer.Values.ShouldBeEmpty();
    }

    [Fact]
    public void ServfailIsNotAnAnswer() {
        // ⚠ The difference that decides the gate's wording: "missing" tells the tenant to publish,
        // "unresolvable" tells them the question could not be asked. Folding SERVFAIL into "missing"
        // would send a tenant to fix a zone that is fine.
        var parsed = DnsMessage.Parse(Message(0x8182, Question("example.com"), []), Id, Txt);

        parsed.Answer.Resolved.ShouldBeFalse();
        parsed.Answer.Error.ShouldContain("RCODE 2");
    }

    [Fact]
    public void ATruncatedAnswerSaysSoEvenWhenItRunsOutPartWay() {
        var whole = Answer(Txt, Question("example.com"), Txt, TxtData("v=spf1 -all"));
        var cut = whole[..^6];
        cut[2] |= 0x02; // TC

        var parsed = DnsMessage.Parse(cut, Id, Txt);

        parsed.Truncated.ShouldBeTrue();
        parsed.Answer.Resolved.ShouldBeFalse();
    }

    [Fact]
    public void ACompressionLoopIsRefusedRatherThanFollowedForever() {
        // ⚠ A pointer at itself. RFC 1035 has no rule against sending one, so the reader has its own.
        var question = Question("example.com");

        // The answer's name sits after the header, the question and its type and class — and is a
        // pointer to exactly there.
        var at = 12 + question.Length + 4;
        var message = Message(0x8180, question, [Record(0xC000 | at, Txt, TxtData("x"))]);

        var parsed = DnsMessage.Parse(message, Id, Txt);

        parsed.Answer.Resolved.ShouldBeFalse();
        parsed.Answer.Error.ShouldContain("loop");
    }

    [Fact]
    public void AnAnswerToSomebodyElsesQuestionIsIgnored() {
        var parsed = DnsMessage.Parse(Answer(Txt, Question("example.com"), Txt, TxtData("x")), 0x9999, Txt);

        parsed.Answer.Resolved.ShouldBeFalse();
    }

    // ── Building answers ──────────────────────────────────────────────────────────────────────

    static byte[] Question(string name) {
        var bytes = new List<byte>();

        foreach (var label in name.Split('.')) {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);

        return [.. bytes];
    }

    static byte[] TxtData(params string[] strings) {
        var bytes = new List<byte>();

        foreach (var text in strings) {
            bytes.Add((byte)text.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(text));
        }

        return [.. bytes];
    }

    /// <summary>One resource record whose name is the two-byte pointer <paramref name="pointer" />.</summary>
    static byte[] Record(int pointer, ushort type, byte[] data) =>
        [
            (byte)(pointer >> 8), (byte)pointer, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 1, 0,
            (byte)(data.Length >> 8), (byte)data.Length, .. data
        ];

    static byte[] Answer(ushort questionType, byte[] question, ushort type, byte[] data) =>
        Message(0x8180, question, [Record(0xC00C, type, data)], questionType);

    static byte[] Message(ushort flags, byte[] question, byte[][] answers, ushort questionType = Txt) =>
        [
            Id >> 8, Id & 0xFF, (byte)(flags >> 8), (byte)flags, 0, 1, 0, (byte)answers.Length, 0, 0, 0, 0,
            .. question, (byte)(questionType >> 8), (byte)questionType, 0, 1,
            .. answers.SelectMany(static x => x)
        ];
}
