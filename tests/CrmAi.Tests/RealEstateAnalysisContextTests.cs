using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Net;
using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.RabbitMq;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.Options;

namespace CrmAi.Tests;

public sealed class RealEstateAnalysisContextTests
{
    private static readonly DateTime Now = new(2026,10,15,15,0,0,DateTimeKind.Utc);
    internal const string Tenant = "00000000-0000-0000-0000-000000000001";
    internal const string Opportunity = "00000000-0000-0000-0000-000000000002";

    [Fact]
    public void T23_Old_event_and_context_deserialize_without_optional_real_estate()
    {
        var legacy = new { eventId="legacy",type="opportunity.updated",occurredAt=Now,opportunityId=Opportunity,userId=(string?)null,data=new { } };
        var parsed = Deserialize(legacy);
        Assert.Null(parsed.CompanyId);
        Assert.True(OpportunityEventTenantPolicy.Matches(parsed,Context().Opportunity));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var oldContext = JsonSerializer.SerializeToNode(Context(),options)!.AsObject();
        oldContext.Remove("realEstate");
        oldContext.Remove("meetingAudioAnalyses");
        var restored = JsonSerializer.Deserialize<OpportunityAnalysisContext>(oldContext.ToJsonString(),options)!;
        Assert.Null(restored.RealEstate);
        Assert.Null(Build(restored,RealEstateAnalysisContextPolicy.ContextKeys).RealEstate);
    }

    [Fact]
    public void T23_New_envelope_is_retained_and_payload_tenant_cannot_override_stored_tenant()
    {
        var context = Context();
        var parsed = Deserialize(new { eventId="current",type="opportunity.updated",occurredAt=Now,opportunityId=Opportunity,companyId=Tenant,data=new { } });
        Assert.Equal(Tenant,parsed.CompanyId);
        Assert.True(OpportunityEventTenantPolicy.Matches(parsed,context.Opportunity));
        Assert.False(OpportunityEventTenantPolicy.Matches(parsed with { CompanyId=Guid.NewGuid().ToString() },context.Opportunity));
        Assert.False(OpportunityEventTenantPolicy.Matches(parsed with { Data=new Dictionary<string,object?> { ["companyId"]=Guid.NewGuid().ToString() } },context.Opportunity));
        Assert.False(OpportunityEventTenantPolicy.Matches(parsed with { CompanyId="malformed" },context.Opportunity));
        Assert.False(OpportunityEventTenantPolicy.Matches(parsed with { OpportunityId=Guid.NewGuid().ToString() },context.Opportunity));
    }

    [Fact]
    public void T23_Optional_blocks_are_opt_in_independently_for_each_builder()
    {
        var context = Context() with { RealEstate=Snapshot() };
        Assert.Null(Build(context,[]).RealEstate);
        Assert.Null(WhatsappConversationAnalysisInput.FromContext(context,[]).RealEstate);
        var input = Build(context,["real_estate_demand"]).RealEstate!;
        Assert.Single(input.Demands);
        Assert.Empty(input.Offers);
        Assert.Empty(input.Visits);
        Assert.Empty(input.Proposals);
        Assert.Empty(input.Knowledge);
        Assert.All(input.Evidence,row=>Assert.Equal("demand",row.Kind));
        var conversation = WhatsappConversationAnalysisInput.FromContext(context,["real_estate_offers"]).RealEstate!;
        Assert.Single(conversation.Offers);
        Assert.Empty(conversation.Demands);
    }

    [Fact]
    public void T03_Context_of_another_tenant_is_rejected_even_if_injected_after_repository()
    {
        var context = Context() with { RealEstate=Snapshot() with { CompanyId=Guid.NewGuid().ToString() } };
        Assert.Null(Build(context,RealEstateAnalysisContextPolicy.ContextKeys).RealEstate);
    }

    [Fact]
    public void Snapshots_are_bounded_keep_cost_unknown_and_mark_expired_evidence()
    {
        var snapshot = Snapshot();
        var context = Context() with { RealEstate=snapshot with
        {
            Offers=Enumerable.Range(0,50).Select(i=>snapshot.Offers.First() with { Id=$"offer-{i}",PublicLocation=new string('x',900),ValidUntil=Now.AddDays(-1) }).ToArray(),
            Knowledge=[new("item","version",3,new string('t',500),new string('e',5000),Now.AddHours(-1),null)]
        } };
        var input = Build(context,RealEstateAnalysisContextPolicy.ContextKeys).RealEstate!;
        Assert.Equal(10,input.Offers.Count);
        Assert.All(input.Offers,row=>{Assert.Equal(200,row.PublicLocation.Length);Assert.Null(row.Condominium);Assert.Equal(0m,row.PropertyTax);});
        Assert.All(input.Evidence.Where(row=>row.Kind=="offer"),row=>Assert.False(row.IsCurrent));
        Assert.Equal(600,Assert.Single(input.Knowledge).Excerpt.Length);
        Assert.Equal(160,Assert.Single(input.Knowledge).Title.Length);
        Assert.Equal("knowledge:version:v3",Assert.Single(input.Evidence,row=>row.Kind=="knowledge").Id);
        var json = JsonSerializer.Serialize(input,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("privateAddress",json);
        Assert.DoesNotContain("document",json,StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("apartment", 80, 300, 500, 80)]
    [InlineData("land", 80, 300, 500, 300)]
    [InlineData("rural", 80, null, 500, 500)]
    [InlineData("apartment", null, 300, 500, 500)]
    [InlineData("apartment", null, 300, null, null)]
    public void Registration_Input_Recomputes_Comparable_Area_And_Preserves_Unknown_Features(string type, int? privateArea, int? landArea, int? legacyArea, int? expected)
    {
        var snapshot=Snapshot();
        var source=snapshot.Offers.First() with { PropertyType=type,PrivateArea=privateArea,LandArea=landArea,Area=legacyArea,TotalArea=999,
            ComparableArea=999,Features=new Dictionary<string,bool?> { ["balcony"]=true,["pool"]=null,["furnished"]=false,["private_label"]=true } };
        var input=Build(Context() with { RealEstate=snapshot with { Offers=[source] } },["real_estate_offers"]).RealEstate!;
        var offer=Assert.Single(input.Offers);
        Assert.Equal((decimal?)expected,offer.ComparableArea);
        Assert.Null(offer.Features!["pool"]); Assert.False(offer.Features["furnished"]); Assert.True(offer.Features["balcony"]);
        Assert.False(offer.Features.ContainsKey("private_label"));
    }

    [Fact]
    public void Future_and_expired_knowledge_never_enters_prompt_and_empty_offers_remain_explicit()
    {
        var context = Context() with { RealEstate=Snapshot() with { Offers=[],Knowledge=
        [new("future","v1",1,"Future","hidden",Now.AddDays(1),null),new("expired","v2",1,"Expired","hidden",Now.AddDays(-1),Now.AddSeconds(-1))] } };
        var input = Build(context,RealEstateAnalysisContextPolicy.ContextKeys).RealEstate!;
        Assert.Empty(input.Offers);
        Assert.Empty(input.Knowledge);
        Assert.Single(input.Demands);
        Assert.DoesNotContain(input.Evidence,row=>row.Kind=="offer");
    }

    [Fact]
    public void Findings_without_known_evidence_are_discarded_and_stale_sources_require_revalidation()
    {
        var context = Context() with { RealEstate=Snapshot() with { Offers=[Snapshot().Offers.First() with { ValidUntil=Now.AddDays(-1) }] } };
        var input = Build(context,["real_estate_offers"]).RealEstate!;
        var source = Assert.Single(input.Evidence);
        var result = RealEstateAnalysisContextPolicy.ValidateFindings(input,
        [new("availability","Revalidar oferta","Confirmar com corretor",[source.Id]),new("invented","Fake","Fake",["invented"]),new("unsupported","Fake","Fake",[])]);
        var finding = Assert.Single(result!);
        Assert.False(Assert.Single(finding.Evidence).IsCurrent);
        Assert.True(finding.RequiresOperationalRevalidation);
        Assert.Null(RealEstateAnalysisContextPolicy.ValidateFindings(null,[new("fake","Fake","Fake",[source.Id])]));
    }

    [Fact]
    public void Qualification_evidence_uses_its_own_revision_timestamp_instead_of_older_demand_time()
    {
        var context=Context() with { RealEstate=Snapshot() with { Demands=[Snapshot().Demands.First() with
            { QualificationStatus="pending_information",CriteriaVersion=4,QualificationRevision=7,UpdatedAt=Now.AddDays(-10),QualificationUpdatedAt=Now.AddHours(-1) }] } };
        var input=Build(context,["real_estate_demand"]).RealEstate!;
        var evidence=Assert.Single(input.Evidence,row=>row.Kind=="qualification");
        Assert.Equal(Now.AddHours(-1),evidence.ObservedAt);
        Assert.Equal(7,evidence.Revision);
        Assert.Contains("criterios v4",evidence.Summary);
    }

    [Fact]
    public void Real_estate_event_subscription_is_opt_in_and_never_counts_as_a_commercial_goal_event()
    {
        var disabled=new RabbitMqOptions();
        Assert.DoesNotContain(disabled.OpportunityAnalysisExchangeNames,value=>value.EndsWith("opportunity.real_estate.updated",StringComparison.Ordinal));
        var enabled=new RabbitMqOptions { RealEstateAnalysisEventsEnabled=true };
        Assert.Single(enabled.OpportunityAnalysisExchangeNames,value=>value.EndsWith("opportunity.real_estate.updated",StringComparison.Ordinal));
        Assert.DoesNotContain(enabled.DailyCheckinExchangeNames,value=>value.EndsWith("opportunity.real_estate.updated",StringComparison.Ordinal));
        Assert.DoesNotContain(enabled.GamificationExchangeNames,value=>value.EndsWith("opportunity.real_estate.updated",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_schema_and_observability_include_sources_only_when_context_is_enabled(bool withContext)
    {
        var context=Context() with { RealEstate=withContext ? Snapshot() : null };
        var input=Build(context,RealEstateAnalysisContextPolicy.ContextKeys);
        var handler=new Handler(HttpStatusCode.OK);
        var logs=new Logs();
        var client=new OpenAiResponsesRiskAnalysisClient(new HttpClient(handler),Options.Create(new OpenAiRiskAnalysisOptions()),logs);
        var runtime=new AiAgentRuntimeSettings("risk-analysis",true,"openai","configured-model","fixture-key","Fixture",1,null,RealEstateAnalysisContextPolicy.ContextKeys);
        await client.AnalyzeAsync(runtime,input,new AiAgentInvocationContext("opportunity",CompanyId:Tenant,OpportunityId:Opportunity),default);
        using var request=JsonDocument.Parse(handler.Request!);
        var schema=request.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
        Assert.Equal(withContext,schema.GetProperty("properties").TryGetProperty("realEstateFindings",out _));
        Assert.Equal(withContext,JsonDocument.Parse(request.RootElement.GetProperty("input").GetString()!).RootElement.TryGetProperty("realEstate",out _));
        var entry=Assert.Single(logs.Entries);
        Assert.Equal(Tenant,entry.Context.CompanyId);
        Assert.Equal(withContext,entry.Context.Metadata!.ContainsKey("realEstateModuleRevision"));
        Assert.Equal("configured-model",request.RootElement.GetProperty("model").GetString());
        if(withContext)
        {
            Assert.Contains("demand:demand:r3",Assert.IsType<string[]>(entry.Context.Metadata["realEstateEvidenceIds"]));
            Assert.Contains("nao confirma agenda",request.RootElement.GetProperty("instructions").GetString());
            var compact=RiskMeetingContextOptimization.Build(input);
            Assert.Equal(JsonDocument.Parse(compact.FullJson).RootElement.GetProperty("realEstate").GetRawText(),
                JsonDocument.Parse(compact.CompactJson).RootElement.GetProperty("realEstate").GetRawText());
        }
    }

    [Fact]
    public async Task Provider_failure_retains_tenant_correlation_and_real_estate_sources_in_existing_log()
    {
        var input=Build(Context() with { RealEstate=Snapshot() },["real_estate_demand"]);
        var handler=new Handler(HttpStatusCode.ServiceUnavailable);
        var logs=new Logs();
        var client=new OpenAiResponsesRiskAnalysisClient(new HttpClient(handler),Options.Create(new OpenAiRiskAnalysisOptions()),logs);
        var runtime=new AiAgentRuntimeSettings("risk-analysis",true,"openai","configured-model","fixture-key","Fixture",1,null,["real_estate_demand"]);
        await Assert.ThrowsAsync<OpenAiRequestException>(()=>client.AnalyzeAsync(runtime,input,
            new AiAgentInvocationContext("opportunity",CompanyId:Tenant,OpportunityId:Opportunity,Metadata:new Dictionary<string,object?> { ["triggerEventId"]="event" }),default));
        var entry=Assert.Single(logs.Entries);
        Assert.Equal(Tenant,entry.Context.CompanyId);
        Assert.Equal("event",entry.Context.Metadata!["triggerEventId"]);
        Assert.Equal(2,entry.Context.Metadata["realEstateModuleRevision"]);
    }

    internal static OpportunityAnalysisContext Context() => new(
        new(Opportunity,Tenant,"Fixture","00000000-0000-0000-0000-000000000003","00000000-0000-0000-0000-000000000004",null,null,100m,"active",false,Now.AddDays(-1),Now,null),
        new("00000000-0000-0000-0000-000000000004","Stage",1),[],[],[],[],[],null,[],[],[],
        new("event","opportunity.updated",Now,Opportunity,null,new Dictionary<string,object?>()));

    internal static RealEstateAnalysisContext Snapshot() => new(Tenant,2,Now,
        [new("demand","rent","active","apartment","City","District",null,2000m,3,Now)],
        [new("offer","property","AP0007","apartment","District, City","rent",1800m,"BRL",null,0,"annual","available",4,Now,Now,null)],[],[],[]);

    private static RiskAnalysisAgentInput Build(OpportunityAnalysisContext context,IReadOnlyCollection<string> keys)
        => new RiskAnalysisAgentInputBuilder(new CommercialRuleAssessmentService()).Build(context,keys).Input;

    private static OpportunityEvent Deserialize(object payload)
    {
        var args = new BasicDeliverEventArgs { Body=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),Exchange="fixture" };
        return (OpportunityEvent)typeof(RabbitMqOpportunityEventConsumerBase).GetMethod("DeserializeEvent",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[args])!;
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Request=await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(status) { Content=new StringContent(JsonSerializer.Serialize(new
            {
                usage=new { input_tokens=123,output_tokens=20 },
                output=new[] { new { content=new[] { new { type="output_text",text="{\"riskLevel\":\"LOW\",\"riskScore\":10,\"reasons\":[\"Fixture\"],\"recommendations\":[\"Fixture\"],\"realEstateFindings\":[]}" } } } }
            })) };
        }
    }
    private sealed class Logs : IAiAgentInvocationLogStore
    {
        public List<AiAgentInvocationLogEntry> Entries {get;}=[];
        public Task SaveAsync(AiAgentInvocationLogEntry entry,CancellationToken cancellationToken) { Entries.Add(entry);return Task.CompletedTask; }
    }
}
