using Warranty.AI.Gateway.Redaction;
using Warranty.Application.Abstractions.AI;

namespace Warranty.UnitTests.Gateway;

public sealed class RegexPiiRedactorTests
{
    private readonly RegexPiiRedactor _redactor = new();

    [Theory]
    [InlineData("Mail me at jane.doe@example.test please.", "Mail me at [EMAIL] please.")]
    [InlineData("<first.last+claims@mail.example.co.uk>", "<[EMAIL]>")]
    [InlineData("Contact: J_Doe-99@sub-domain.example.test.", "Contact: [EMAIL].")]
    public void Email_addresses_become_placeholders(string text, string expected)
    {
        var result = _redactor.Redact(text);

        result.Text.ShouldBe(expected);
        result.Replacements.ShouldBe(1);
    }

    [Theory]
    [InlineData("Call +47 412 34 567 after five.")]
    [InlineData("Call +4741234567 after five.")]
    [InlineData("Call 0047 412 34 567 after five.")]
    [InlineData("Call +1 (555) 123-4567 after five.")]
    [InlineData("Call +44 (0)20 7946 0958 after five.")]
    [InlineData("Call +33 1 23 45 67 89 after five.")]
    [InlineData("Call 555-123-4567 after five.")]
    [InlineData("Call (555) 123 4567 after five.")]
    [InlineData("Call 555.123.4567 after five.")]
    [InlineData("Call 412 34 567 after five.")]
    [InlineData("Call 020 7946 0958 after five.")]
    public void Phone_numbers_become_placeholders(string text)
    {
        var result = _redactor.Redact(text);

        result.Text.ShouldBe("Call [PHONE] after five.");
        result.Replacements.ShouldBe(1);
    }

    [Theory]
    [InlineData("Serial SN-48213377 stopped working.")]
    [InlineData("Serial 4821337799 stopped working.")]
    [InlineData("Invoice INV/2026/00123456 attached.")]
    [InlineData("Bought on 2026-03-14 and 14.03.2026.")]
    [InlineData("Failed at 2026-10-03T15:31:51Z.")]
    [InlineData("Paid 12 500.00 NOK, then 1 250 000.00 NOK.")]
    [InlineData("Paid 1.250.000,00 NOK.")]
    [InlineData("Firmware 10.0.26200 is installed.")]
    [InlineData("Claim 3f2a1b9c-1234-5678-9abc-def012345678 is open.")]
    [InlineData("Call 555 1234 later.")]
    [InlineData("Short +47 123 is not a number.")]
    public void Serials_dates_amounts_and_identifiers_are_kept(string text)
    {
        var result = _redactor.Redact(text);

        result.Text.ShouldBe(text);
        result.Replacements.ShouldBe(0);
    }

    [Fact]
    public void Every_occurrence_is_replaced_and_counted()
    {
        var result = _redactor.Redact(
            "Reach jane@example.test or +47 412 34 567; backup john@example.test, 555-123-4567. Serial SN-48213377.");

        result.Text.ShouldBe("Reach [EMAIL] or [PHONE]; backup [EMAIL], [PHONE]. Serial SN-48213377.");
        result.Replacements.ShouldBe(4);
    }

    [Fact]
    public void Digits_inside_an_email_address_are_not_a_second_match()
    {
        var result = _redactor.Redact("Write to 5551234567@example.test or 555.123.4567@example.test.");

        result.Text.ShouldBe("Write to [EMAIL] or [EMAIL].");
        result.Replacements.ShouldBe(2);
    }

    [Fact]
    public void Text_without_personal_data_is_returned_unchanged()
    {
        _redactor.Redact(string.Empty).ShouldBe(new RedactionResult(string.Empty, 0));
        _redactor.Redact("The compressor rattles at start-up.").ShouldBe(new RedactionResult("The compressor rattles at start-up.", 0));
    }
}
