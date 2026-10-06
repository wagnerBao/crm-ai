using System.Net;
using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using Microsoft.Extensions.Options;

namespace CrmAi.Tests;

public sealed class WhatsappConversationParticipantTests
{
    [Fact]
    public void ContactOnly_input_keeps_contact_and_internal_owner_separate()
    {
        var input = WhatsappConversationAnalysisInput.FromContactEvent(CreateEvent());

        Assert.Equal("contact-pierre", input.Participants!.ContactId);
        Assert.Equal("Jean Pierre", input.Participants.ContactName);
        Assert.Equal("user-diego", input.Participants.OwnerUserId);
        Assert.Equal("Diego Gonçalves", input.Participants.OwnerUserName);
        Assert.Equal("Diego - 553171452764", input.Participants.MailboxName);
        Assert.Empty(input.Users);
    }

    [Fact]
    public void Opportunity_input_resolves_names_by_id_even_when_optional_entity_lists_are_disabled()
    {
        var evt = CreateEvent() with
        {
            Data = new Dictionary<string, object?>
            {
                ["contactId"] = "contact-pierre",
                ["ownerUserId"] = "user-diego",
                ["text"] = "Equipe: Pierre, troca o código do botão?"
            }
        };
        var now = DateTime.UtcNow;
        var context = new OpportunityAnalysisContext(
            new OpportunitySnapshot("opportunity", "company", "Venda", "pipeline", "stage", null, "another-owner", 0, "active", false, now, now, null),
            new PipelineStageSnapshot("stage", "Contato", 1), [], [],
            [new ContactSnapshot("another-contact", null, "Outro contato", "", "", null, null, "active"),
             new ContactSnapshot("contact-pierre", null, "Jean Pierre", "", "", null, "user-diego", "active")],
            [new UserSnapshot("another-owner", "Outro responsável", "", true),
             new UserSnapshot("user-diego", "Diego Gonçalves", "", true)],
            [], null, [], [], [], evt);

        var input = WhatsappConversationAnalysisInput.FromContext(context, []);

        Assert.Empty(input.Users);
        Assert.Empty(input.Contacts);
        Assert.Equal("Jean Pierre", input.Participants!.ContactName);
        Assert.Equal("Diego Gonçalves", input.Participants.OwnerUserName);
    }

    [Fact]
    public void Missing_identity_is_not_invented_from_the_message_text()
    {
        var evt = CreateEvent() with { UserId = null, Data = new Dictionary<string, object?> { ["text"] = "Diego, já troquei o botão." } };
        var input = WhatsappConversationAnalysisInput.FromContactEvent(evt);

        Assert.Null(input.Participants!.ContactName);
        Assert.Null(input.Participants.OwnerUserName);
        Assert.Null(input.Participants.OwnerUserId);
    }

    [Fact]
    public async Task OpenAi_request_includes_participant_identity_and_team_perspective_with_a_custom_prompt()
    {
        var handler = new CapturingHandler();
        var client = new OpenAiResponsesWhatsappConversationAnalysisClient(
            new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), new NullInvocationLogStore());
        var settings = new AiAgentRuntimeSettings("whatsapp-conversation-analysis", true, "openai", "gpt-4.1-mini", "test-key", "Prompt personalizado da empresa.", 10, null, []);

        await client.AnalyzeAsync(settings, WhatsappConversationAnalysisInput.FromContactEvent(CreateEvent()), AiAgentInvocationContext.Unknown, CancellationToken.None);

        using var request = JsonDocument.Parse(handler.Body!);
        using var input = JsonDocument.Parse(request.RootElement.GetProperty("input").GetString()!);
        var participants = input.RootElement.GetProperty("participants");
        Assert.Equal("Jean Pierre", participants.GetProperty("contactName").GetString());
        Assert.Equal("Diego Gonçalves", participants.GetProperty("ownerUserName").GetString());
        var instructions = request.RootElement.GetProperty("instructions").GetString()!;
        Assert.Contains(settings.Instructions, instructions);
        Assert.Contains("A direcao prevalece sobre nomes de remetente", instructions);
        Assert.Contains("nunca ao proprio responsavel", instructions);
        Assert.Contains("Um pedido outgoing da equipe ao contato nao e um pedido recebido do cliente", instructions);
        Assert.Contains("previousSummary e existingSuggestions podem conter atribuicoes antigas incorretas", instructions);
    }

    private static OpportunityEvent CreateEvent() => new(
        "event", "opportunity.whatsapp.conversation.batch", DateTime.UtcNow, Guid.Empty.ToString(), "user-diego",
        new Dictionary<string, object?>
        {
            ["conversationId"] = "conversation",
            ["contactId"] = "contact-pierre",
            ["contactName"] = "Jean Pierre",
            ["ownerUserId"] = "user-diego",
            ["ownerUserName"] = "Diego Gonçalves",
            ["mailboxName"] = "Diego - 553171452764",
            ["text"] = "[2026-09-15 16:03] Equipe - caixa: Diego: Pierre, troca o código do botão?\n[2026-09-15 16:04] Cliente - Jean Pierre: Já troquei, pode testar."
        });

    [Theory]
    [InlineData("whatsapp", true)]
    [InlineData("instagram", false)]
    public async Task Attendance_rules_cover_unanswered_turns_only_for_whatsapp(string platformArea, bool usesAttendanceRules)
    {
        var handler = new CapturingHandler();
        var client = new OpenAiResponsesWhatsappConversationAnalysisClient(
            new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), new NullInvocationLogStore());
        var settings = new AiAgentRuntimeSettings("whatsapp-conversation-analysis", true, "openai", "gpt-4.1-mini", "test-key", "Prompt personalizado.", 10, null, []);

        await client.AnalyzeAsync(settings, WhatsappConversationAnalysisInput.FromContactEvent(CreateEvent()),
            AiAgentInvocationContext.Unknown with { PlatformArea = platformArea }, CancellationToken.None);

        using var request = JsonDocument.Parse(handler.Body!);
        var instructions = request.RootElement.GetProperty("instructions").GetString()!;
        if (usesAttendanceRules)
        {
            Assert.Contains("todo o ultimo turno do cliente", instructions);
            Assert.Contains("O envio de placa, documento ou dado solicitado", instructions);
            Assert.Contains("Uma saudacao que inicia ou retoma contato", instructions);
            Assert.Contains("Uma sugestao existente deduplica o registro, mas nao atende o cliente", instructions);
            Assert.Contains("Comunicados automaticos, publicidade e spam", instructions);
            Assert.DoesNotContain("ultima mensagem for apenas agradecimento, emoji, saudacao", instructions);
        }
        else
        {
            Assert.DoesNotContain("todo o ultimo turno do cliente", instructions);
            Assert.Contains("ultima mensagem for apenas agradecimento, emoji, saudacao", instructions);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var result = new OpenAiWhatsappConversationAnalysisResponse("Botão corrigido.", false, null, false, null, null, null, 90, []);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    output = new[] { new { content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } } }
                }))
            };
        }
    }

    private sealed class NullInvocationLogStore : IAiAgentInvocationLogStore
    {
        public Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
