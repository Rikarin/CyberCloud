using System.Diagnostics;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The password hash a mailbox's login is checked against, and the line Dovecot reads it from.
/// </summary>
/// <remarks>
///     ⚠ <b>Every expected hash here came out of a second implementation.</b> The first two are the
///     SHA-crypt specification's own test vectors; all five were reproduced by OpenSSL 3.5.5's
///     <c>openssl passwd -6</c> on 2026-09-23 before they were written down. A hand-written crypt that
///     agreed only with itself would be a password file no Dovecot accepts, and the symptom would be
///     every login refused with nothing to say why.
///     <para>
///         ⚠ <b>The non-ASCII vector was computed twice, and the first was wrong.</b> Passed as an
///         argument through a Windows console, <c>pässwörd</c> reached OpenSSL in the console's code
///         page rather than UTF-8 and hashed to something no mailbox would ever match; this test caught
///         it. The value below was computed from the password's UTF-8 bytes on standard input — the
///         bytes Dovecot is handed by an IMAP or SMTP client, and the bytes <see cref="Sha512Crypt" />
///         hashes.
///     </para>
/// </remarks>
public sealed class MailPasswordTests {
    [Theory]
    [InlineData("Hello world!", "saltstring", null,
        "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    [InlineData("Hello world!", "saltstringsaltstring", 10000,
        "$6$rounds=10000$saltstringsaltst$OW1/O6BYHV6BcXZu8QVeXbDWra3Oeqh0sbHbbMCVNSnCM/UrjmM0Dp8vOuZeHBy/YTBmSK6H9qs/y3RnOaw5v.")]
    [InlineData("This is just a test", "toolongsaltstring", 5000,
        "$6$rounds=5000$toolongsaltstrin$lQ8jolhgVRVhY4b5pZKaysCLi0QBxGoNeKQzQ3glMhwllF7oGDZxUhx1yxdYcz/e1JSbq3y6JMxxl8audkUEm0")]
    [InlineData("correct horse battery staple", "abcdefghijklmnop", 100000,
        "$6$rounds=100000$abcdefghijklmnop$237b4Fqq64YNpA09/be0YRDDrONH67p8fe4qSYY8UxMhW7je1jh8c21qMmT2IaXGvbGXIRQabfQKyXUpkYkpg.")]
    [InlineData("pässwörd", "short", null,
        "$6$short$8PxE01Domb6dcxeQtCxLibybOXj32CQeToNgt9Pwl9wf/GmpnVKsiWm8Srr87eCmFRgpWY3tWNsL1YIVCAfz31")]
    public void TheHashIsWhatCryptProducesForTheSameInput(string password, string salt, int? rounds, string expected) =>
        Sha512Crypt.Hash(password, salt, rounds).ShouldBe(expected);

    [Fact]
    public void TheSaltIsStablePerMailboxAndDifferentBetweenMailboxes() {
        // ⚠ CLAUSE 1. The hash is rendered every pass; a random salt would make it a different hash
        // every pass, the co-owned fragment would never be Unchanged, and Postfix would reload for
        // nothing forever. Sha512Crypt.SaltFor carries the argument.
        var one = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
        var two = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");

        Sha512Crypt.SaltFor(one).ShouldBe(Sha512Crypt.SaltFor(one));
        Sha512Crypt.SaltFor(one).ShouldNotBe(Sha512Crypt.SaltFor(two));
        Sha512Crypt.SaltFor(one).Length.ShouldBe(16);
        Sha512Crypt.SaltFor(one).ShouldAllBe(static x => char.IsAsciiLetterOrDigit(x) || x == '.' || x == '/');

        // And so two mailboxes with one password hash differently — what a salt is for.
        Sha512Crypt.Hash("same", Sha512Crypt.SaltFor(one), Sha512Crypt.MailboxRounds)
            .ShouldNotBe(Sha512Crypt.Hash("same", Sha512Crypt.SaltFor(two), Sha512Crypt.MailboxRounds));
    }

    [Fact]
    public void AMailboxHashFitsInsideOneReconcilePass() {
        // ⚠ Clause 3 gives a pass thirty seconds and a mailbox pass hashes once. Measured rather than
        // assumed, because MailboxRounds is a number somebody will be tempted to raise.
        var started = Stopwatch.GetTimestamp();

        Sha512Crypt.Hash("a password of ordinary length", Sha512Crypt.SaltFor(Guid.NewGuid()), Sha512Crypt.MailboxRounds);

        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AMailboxWithNoPasswordCannotBeSignedInToRatherThanOpenToAnyPassword() {
        // ⚠ THE LINE THAT MATTERS MOST HERE. Dovecot's passwd-file reads an EMPTY password field as
        // "no password check" — any password signs in. A mailbox with no handle must render a hash
        // nothing produces, and nologin besides; the cluster-backed suite logs in to one and is
        // refused.
        var line = MailMailboxes.PasswdLine("archive@example.com", null, string.Empty);

        line.ShouldBe("archive@example.com:{CRYPT}!::::::nologin=y\n");
        line.Split(':')[1].ShouldNotBeEmpty();
    }

    [Fact]
    public void AQuotaIsWrittenForBothDovecotMajors() {
        // ⚠ 2.4 reads userdb_quota_storage_size and 2.3 reads userdb_quota_rule, each ignoring the
        // other's — both read back with `doveadm quota get` against each image on 2026-09-23. A line
        // with only one would give the other major's domains the default quota, silently.
        var line = MailMailboxes.PasswdLine("alice@example.com", "$6$salt$hash", "5Gi");

        line.ShouldBe(
            "alice@example.com:{SHA512-CRYPT}$6$salt$hash::::::userdb_quota_storage_size=5120M "
            + "userdb_quota_rule=*:storage=5120M\n"
        );
    }

    [Theory]
    [InlineData("1Gi", "1024M")]
    [InlineData("512Mi", "512M")]
    [InlineData("1G", "953M")]
    [InlineData("100Ki", "1M")]
    public void AKubernetesQuantityBecomesWholeMebibytes(string quantity, string expected) =>
        // ⚠ Dovecot's M is a mebibyte. 1G is a decimal gigabyte, 953.67 MiB, and it rounds DOWN —
        // a quota rounded up is a mailbox allowed more than the tenant asked for. Except to zero:
        // 100Ki becomes 1M, because Dovecot reads a zero limit as NO limit.
        MailDomains.DovecotSize(quantity).ShouldBe(expected);
}
