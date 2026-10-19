using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.Persistence;
using Npgsql;

namespace CrmAi.Tests;

public sealed class RealEstatePostgresFactAttribute : FactAttribute
{
    public RealEstatePostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REAL_ESTATE_ANALYSIS_TEST_DB"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REAL_ESTATE_SCHEMA_SCRIPT")))
            Skip="Requires explicit local *_test PostgreSQL and backend schema 320 path; no operational fallback.";
    }
}

public sealed class RealEstateAnalysisDatabaseTests : IAsyncLifetime
{
    private NpgsqlDataSource? source;
    private string? connectionString;
    private string? schema;
    private readonly Settings settings = new();

    public async Task InitializeAsync()
    {
        var value=Environment.GetEnvironmentVariable("REAL_ESTATE_ANALYSIS_TEST_DB");
        if(string.IsNullOrWhiteSpace(value)) return;
        var options=new NpgsqlConnectionStringBuilder(value);
        if(options.Host is not ("127.0.0.1" or "localhost" or "::1") || options.Database?.EndsWith("_test",StringComparison.Ordinal)!=true)
            throw new InvalidOperationException("Real-estate analysis fixtures require explicit local *_test database.");
        connectionString=options.ConnectionString;
        schema="real_estate_analysis_"+Guid.NewGuid().ToString("N");
        await using(var connection=new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"create extension if not exists btree_gist; create schema {schema};",connection).ExecuteNonQueryAsync();
        }
        options.SearchPath=$"{schema},public";
        source=NpgsqlDataSource.Create(options.ConnectionString);
        await Sql("""
            create table companies(id uuid primary key);
            create table users(id uuid primary key,company_id uuid);
            create table contacts(id uuid primary key,company_id uuid);
            create table accounts(id uuid primary key,company_id uuid);
            create table products(id uuid primary key,company_id uuid);
            create table activities(id uuid primary key,company_id uuid);
            create table service_agents(id uuid primary key,profile varchar(40));
            create table service_agent_conversation_states(id uuid primary key,company_id uuid);
            create table service_agent_runs(id uuid primary key);
            create table opportunities(id uuid primary key,company_id uuid,name text,pipeline_id uuid,stage_id uuid,account_id uuid,owner_user_id uuid,value numeric,status text,risk boolean,created_at timestamptz,updated_at timestamptz,last_activity_at timestamptz);
            create view vw_ai_agent_opportunity_context as select id as opportunity_id,company_id,name,pipeline_id,stage_id,account_id,owner_user_id,value,status,risk,created_at,updated_at,last_activity_at,'Fixture stage'::text as stage_title,1 as stage_sort_order from opportunities;
            """);
        var scriptPath=Environment.GetEnvironmentVariable("REAL_ESTATE_SCHEMA_SCRIPT")!;
        await Sql(await File.ReadAllTextAsync(scriptPath));
        await Sql(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(scriptPath)!, "322_real_estate_structured_location.sql")));
        await Sql(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(scriptPath)!, "323_real_estate_commercial_registration.sql")));
        await Sql("""
            insert into companies(id) values('00000000-0000-0000-0000-000000000001'),('10000000-0000-0000-0000-000000000001');
            insert into contacts values('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000001'),('10000000-0000-0000-0000-000000000005','10000000-0000-0000-0000-000000000001');
            insert into opportunities values('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Fixture','00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000004',null,null,100,'active',false,now(),now(),null),('10000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000001','Other tenant','10000000-0000-0000-0000-000000000003','10000000-0000-0000-0000-000000000004',null,null,100,'active',false,now(),now(),null);
            insert into company_modules(company_id,module_key,state,revision) values('00000000-0000-0000-0000-000000000001','real_estate','enabled',7);
            insert into real_estate_settings_versions(company_id,version,status,configuration) values('00000000-0000-0000-0000-000000000001',1,'published','{"disclosure":{"addressPrecision":"city"}}');
            insert into real_estate_demands(id,company_id,contact_id,opportunity_id,purpose,data) values('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000002','rent','{"city":"Fixture city","neighborhood":"Fixture district","propertyTypes":["apartment"],"budgetMax":2000,"notes":"PRIVATE DEMAND NOTE"}'),('10000000-0000-0000-0000-000000000006','10000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000005','10000000-0000-0000-0000-000000000002','sale','{"city":"FOREIGN TENANT"}');
            insert into real_estate_qualifications(company_id,demand_id,criteria_version,status) values('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000006',2,'no_match');
            insert into real_estate_properties(id,company_id,code,normalized_code,title,property_type,city,neighborhood,status,disclosure_authorized,portfolio_confirmed_at,data) values('00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000001','AP0007','AP0007','Fixture apartment','apartment','Fixture city','Fixture district','available',true,now(),'{"privateAddress":"SECRET STREET 123","publicLocation":"SECRET STREET 123"}');
            insert into real_estate_offers(id,company_id,property_id,purpose,amount,currency,condominium_fee,iptu,iptu_periodicity,status) values('00000000-0000-0000-0000-000000000008','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000007','rent',1800,'BRL',null,0,'annual','available');
            insert into real_estate_opportunity_properties(company_id,opportunity_id,property_id,offer_id,interest) values('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000008','presented');
            insert into real_estate_visits(id,company_id,contact_id,property_id,offer_id,opportunity_id,status,data) values('00000000-0000-0000-0000-000000000009','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000008','00000000-0000-0000-0000-000000000002','requested','{"notes":"PRIVATE VISIT NOTE"}');
            insert into real_estate_proposals(company_id,opportunity_id,property_id,offer_id,amount,status) values('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000008',1700,'submitted');
            insert into company_knowledge_items(id,company_id,item_key,title,module_key,published_version,draft) values('00000000-0000-0000-0000-000000000010','00000000-0000-0000-0000-000000000001','rent','Locação','real_estate',1,'{}');
            insert into company_knowledge_versions(company_id,item_id,version,status,title,content,visibility,data) values('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000010',1,'published','Locação','Locação exige confirmação humana da visita.','customer_safe','{}');
            """);
    }

    public async Task DisposeAsync()
    {
        if(source is not null) await source.DisposeAsync();
        if(connectionString is null || schema is null) return;
        await using var connection=new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"drop schema {schema} cascade",connection).ExecuteNonQueryAsync();
    }

    [RealEstatePostgresFact]
    public async Task T03_T23_Loads_only_explicit_public_tenant_linked_projection_from_actual_schema()
    {
        var context=await Read();
        var snapshot=Assert.IsType<RealEstateAnalysisContext>(context!.RealEstate);
        Assert.Equal(7,snapshot.ModuleRevision);
        Assert.Equal("no_match",Assert.Single(snapshot.Demands).QualificationStatus);
        var offer=Assert.Single(snapshot.Offers);
        Assert.Equal("AP0007",offer.Code);
        Assert.Equal("Fixture city",offer.PublicLocation);
        Assert.Null(offer.Condominium);
        Assert.Equal(0m,offer.PropertyTax);
        Assert.Equal("requested",Assert.Single(snapshot.Visits).Status);
        Assert.Single(snapshot.Proposals);
        Assert.Single(snapshot.Knowledge);
        var json=System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("PRIVATE",json);
        Assert.DoesNotContain("SECRET STREET",json);
        Assert.DoesNotContain("FOREIGN TENANT",json);
    }

    [RealEstatePostgresFact]
    public async Task Structured_location_obeys_both_visibility_caps_before_entering_analysis_input()
    {
        await Sql("""
            update real_estate_properties set state_code='SC',city_id='4205407',postal_code='88015731',
              street='Rua Sintetica',street_number='731-A',address_complement='COMPLEMENTO EXATO',
              latitude=-27.5945678,longitude=-48.5481234,maps_place_id='LOCAL-EXATO-731',
              block='BLOCO INTERNO',plot='QUADRA INTERNA',condominium_name='CONDOMINIO INTERNO',building_name='EDIFICIO INTERNO';
            """);
        var precisions=new[] { "city","neighborhood","street","full" };
        foreach(var companyPrecision in precisions)
        foreach(var propertyPrecision in precisions.Skip(1))
        {
            await Sql($"update real_estate_properties set address_visibility='{propertyPrecision}'; update real_estate_settings_versions set configuration=jsonb_set(configuration,'{{disclosure,addressPrecision}}',to_jsonb('{companyPrecision}'::text));");
            var context=(await Read())!;
            var input=RealEstateAnalysisContextPolicy.Select(context,RealEstateAnalysisContextPolicy.ContextKeys,DateTime.UtcNow)!;
            var offer=Assert.Single(input.Offers); var location=Assert.IsType<CrmRestApi.Domain.PublicRealEstateLocation>(offer.Location);
            var rank=Math.Min(Array.IndexOf(precisions,companyPrecision),Array.IndexOf(precisions,propertyPrecision));
            Assert.Equal(precisions[rank],location.AddressVisibility);
            Assert.Equal(rank>=1 ? "Fixture district" : null,location.Neighborhood);
            Assert.Equal(rank>=2 ? "Rua Sintetica" : null,location.Street);
            Assert.Equal(location.PublicLocation,offer.PublicLocation);
            var json=System.Text.Json.JsonSerializer.Serialize(input);
            var map=Uri.UnescapeDataString(location.MapsUrl!);
            if(rank<2) Assert.DoesNotContain("Rua Sintetica",json+map);
            if(rank<3)
                foreach(var exact in new[] { "88015731","731-A","COMPLEMENTO EXATO","-27.5945678","-48.5481234","LOCAL-EXATO-731","query_place_id","BLOCO INTERNO","QUADRA INTERNA","CONDOMINIO INTERNO","EDIFICIO INTERNO" })
                    Assert.DoesNotContain(exact,json+map);
            else
            {
                Assert.Equal("731-A",location.StreetNumber);
                Assert.Contains("-27.5945678,-48.5481234",map);
                Assert.Contains("query_place_id=LOCAL-EXATO-731",map);
                Assert.Equal("BLOCO INTERNO",location.Block);
                Assert.Equal("CONDOMINIO INTERNO",location.CondominiumName);
            }
            Assert.DoesNotContain("SECRET STREET",json); Assert.DoesNotContain("PRIVATE",json);
        }
    }

    [RealEstatePostgresFact]
    public async Task Unconfirmed_exact_location_is_empty_even_for_full_visibility_analysis()
    {
        await Sql("""
            update real_estate_properties set address_visibility='full',street='RUA NAO CONFIRMADA',
              latitude=-27.5945678,longitude=-48.5481234,portfolio_confirmed_at=null;
            update real_estate_settings_versions set configuration='{"disclosure":{"addressPrecision":"full"}}';
            """);
        var offer=Assert.Single((await Read())!.RealEstate!.Offers);
        Assert.Equal(string.Empty,offer.PublicLocation);
        Assert.Null(offer.Location!.MapsUrl);
        Assert.Null(offer.Location.Street);
        Assert.Null(offer.Location.Latitude);
    }

    [RealEstatePostgresFact]
    public async Task Registration_Metadata_And_Demand_Criteria_Enter_Only_Their_Typed_Public_Snapshots()
    {
        await Sql("""
            update real_estate_properties set property_subtype='penthouse',area=500,private_area=80,total_area=700,land_area=300,
              bedrooms=2,suites=1,condition='renovated',occupation='vacant',features='{"balcony":true,"pool":null,"furnished":false}',
              data=data||'{"parkingSpaces":2}'::jsonb,block='BLOCO NAO PUBLICO';
            update real_estate_demands set data=data||'{"propertySubtypes":["penthouse"],"minimumSuites":1,"minimumParkingSpaces":2,"requiredFeatures":["balcony","elevator"],"minimumArea":70}'::jsonb;
            """);
        var context=(await Read())!;
        var input=RealEstateAnalysisContextPolicy.Select(context,RealEstateAnalysisContextPolicy.ContextKeys,DateTime.UtcNow)!;
        var offer=Assert.Single(input.Offers); var demand=Assert.Single(input.Demands);
        Assert.Equal("penthouse",offer.PropertySubtype); Assert.Equal(80m,offer.ComparableArea); Assert.Equal(700m,offer.TotalArea);
        Assert.Equal(1,offer.Suites); Assert.Equal(2,offer.ParkingSpaces); Assert.Equal("renovated",offer.Condition); Assert.Equal("vacant",offer.Occupation);
        Assert.True(offer.Features!["balcony"]); Assert.Null(offer.Features["pool"]); Assert.False(offer.Features["furnished"]);
        Assert.False(offer.Features.ContainsKey("accessible")); Assert.Null(offer.Location!.Block);
        Assert.Equal("penthouse",Assert.Single(demand.PropertySubtypes!)); Assert.Equal(1,demand.MinimumSuites); Assert.Equal(2,demand.MinimumParkingSpaces);
        Assert.Equal(new[] { "balcony","elevator" },demand.RequiredFeatures);
        Assert.DoesNotContain("BLOCO NAO PUBLICO",System.Text.Json.JsonSerializer.Serialize(input));
        await Sql("update real_estate_properties set property_type='land',property_subtype='condominium_lot'");
        Assert.Equal(300m,Assert.Single((await Read())!.RealEstate!.Offers).ComparableArea);
        await Sql("update real_estate_properties set property_type='rural',property_subtype='farm',land_area=null");
        Assert.Equal(500m,Assert.Single((await Read())!.RealEstate!.Offers).ComparableArea);
        await Sql("update real_estate_properties set area=null");
        Assert.Null(Assert.Single((await Read())!.RealEstate!.Offers).ComparableArea);
    }

    [RealEstatePostgresFact]
    public async Task T03_Mismatched_event_is_rejected_before_agent_configuration_and_child_queries()
    {
        var trigger=RealEstateAnalysisContextTests.Context().TriggerEvent with { CompanyId="10000000-0000-0000-0000-000000000001" };
        Assert.Null(await new PostgresOpportunityContextRepository(source!,settings).GetForAnalysisAsync(trigger,default));
        Assert.Equal(0,settings.Calls);
        trigger=trigger with { CompanyId=null,Data=new Dictionary<string,object?> { ["companyId"]="10000000-0000-0000-0000-000000000001" } };
        Assert.Null(await new PostgresOpportunityContextRepository(source!,settings).GetForAnalysisAsync(trigger,default));
        Assert.Equal(0,settings.Calls);
    }

    [RealEstatePostgresFact]
    public async Task T23_Module_disabled_or_unpublished_rules_prevent_all_optional_context()
    {
        await Sql("update company_modules set state='disabled'");
        Assert.Null((await Read())!.RealEstate);
        await Sql("update company_modules set state='enabled'; update real_estate_settings_versions set status='archived'");
        Assert.Null((await Read())!.RealEstate);
    }

    [RealEstatePostgresFact]
    public async Task T23_Disabled_keys_do_not_query_optional_tables_and_inactive_agent_never_loads_them()
    {
        settings.Keys=[];
        await Sql("drop table real_estate_demands cascade");
        Assert.Null((await Read())!.RealEstate);
        settings.Keys=RealEstateAnalysisContextPolicy.ContextKeys;
        settings.Active=false;
        Assert.Null((await Read())!.RealEstate);
    }

    [RealEstatePostgresFact]
    public async Task T05_T06_T10_Authorization_publication_and_opportunity_links_are_enforced()
    {
        await Sql("update real_estate_properties set disclosure_authorized=false; update company_knowledge_versions set visibility='restricted'; delete from real_estate_opportunity_properties");
        var snapshot=(await Read())!.RealEstate!;
        Assert.Empty(snapshot.Offers);
        Assert.Empty(snapshot.Knowledge);
        Assert.Single(snapshot.Demands);
        await Sql("update company_knowledge_versions set visibility='customer_safe',effective_from=now()+interval '1 day'");
        Assert.Empty((await Read())!.RealEstate!.Knowledge);
        await Sql("update company_knowledge_versions set effective_from=null,effective_until=now()-interval '1 day'");
        Assert.Empty((await Read())!.RealEstate!.Knowledge);
    }

    [RealEstatePostgresFact]
    public async Task T05_Public_disclosure_policy_removes_tax_and_draft_offers_from_analysis()
    {
        await Sql("update real_estate_settings_versions set configuration=jsonb_set(configuration,'{disclosure,publishIptu}','false')");
        var offer=Assert.Single((await Read())!.RealEstate!.Offers);
        Assert.Null(offer.PropertyTax);
        Assert.Null(offer.PropertyTaxPeriod);
        await Sql("update real_estate_offers set status='draft'");
        Assert.Empty((await Read())!.RealEstate!.Offers);
    }

    [RealEstatePostgresFact]
    public async Task Sql_limits_the_linked_portfolio_and_excerpts_before_materialization()
    {
        await Sql("""
            insert into real_estate_properties(company_id,code,normalized_code,title,property_type,city,status,disclosure_authorized,portfolio_confirmed_at)
            select '00000000-0000-0000-0000-000000000001','FIX-'||number,'FIX-'||number,'Synthetic apartment','apartment','Fixture city','available',true,now() from generate_series(1,25) number;
            insert into real_estate_offers(company_id,property_id,purpose,amount,status)
            select company_id,id,'rent',1800,'available' from real_estate_properties where code like 'FIX-%';
            insert into real_estate_opportunity_properties(company_id,opportunity_id,property_id,offer_id,interest)
            select company_id,'00000000-0000-0000-0000-000000000002',property_id,id,'presented' from real_estate_offers where id<>'00000000-0000-0000-0000-000000000008';
            update company_knowledge_versions set content=repeat('Locação ',1000);
            """);
        var snapshot=(await Read())!.RealEstate!;
        Assert.Equal(10,snapshot.Offers.Count);
        Assert.Equal(600,Assert.Single(snapshot.Knowledge).Excerpt.Length);
    }

    [RealEstatePostgresFact]
    public async Task T03_Legacy_instagram_batch_cannot_enrich_conversation_with_a_foreign_opportunity()
    {
        await Sql("""
            alter table contacts add column account_id uuid;
            create table instagram_conversations(id uuid primary key,company_id uuid,contact_id uuid,owner_user_id uuid,contact_name text,summary text,channel text);
            insert into instagram_conversations values('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000005',null,'Synthetic customer',null,'instagram');
            """);
        // No ai_insights table, context repository or provider is available: unauthorized
        // channel events must stop before deduplication, settings, enrichment or generation.
        var service=new PostgresInstagramConversationAnalysisService(source!,settings,null!,null!,null!);
        var trigger=RealEstateAnalysisContextTests.Context().TriggerEvent with
        {
            Type="opportunity.instagram.conversation.batch",OpportunityId="10000000-0000-0000-0000-000000000002",
            Data=new Dictionary<string,object?> { ["conversationId"]="00000000-0000-0000-0000-000000000011",["text"]="Synthetic customer asks about an apartment." }
        };
        await service.ProcessAsync(trigger,default);
        await service.ProcessAsync(trigger with { OpportunityId=RealEstateAnalysisContextTests.Opportunity,CompanyId="10000000-0000-0000-0000-000000000001" },default);
        var data=new Dictionary<string,object?>(trigger.Data) { ["companyId"]="10000000-0000-0000-0000-000000000001" };
        await service.ProcessAsync(trigger with { OpportunityId=RealEstateAnalysisContextTests.Opportunity,Data=data },default);
        Assert.Equal(0,settings.Calls);
    }

    [RealEstatePostgresFact]
    public async Task T03_Legacy_whatsapp_conversation_and_opportunity_must_share_stored_tenant()
    {
        await Sql("create table whatsapp_conversations(id uuid primary key,company_id uuid); insert into whatsapp_conversations values('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-000000000001')");
        var trigger=RealEstateAnalysisContextTests.Context().TriggerEvent with
        {
            Type="opportunity.whatsapp.conversation.batch",OpportunityId="10000000-0000-0000-0000-000000000002",
            Data=new Dictionary<string,object?> { ["conversationId"]="00000000-0000-0000-0000-000000000012" }
        };
        Assert.Null(await new PostgresOpportunityContextRepository(source!,settings).GetForAnalysisAsync(trigger,default));
        Assert.Equal(0,settings.Calls);
    }

    private Task<OpportunityAnalysisContext?> Read()=>new PostgresOpportunityContextRepository(source!,settings).GetForAnalysisAsync(RealEstateAnalysisContextTests.Context().TriggerEvent,default);
    private async Task Sql(string sql)
    {
        await using var connection=await source!.OpenConnectionAsync();
        await new NpgsqlCommand(sql,connection).ExecuteNonQueryAsync();
    }
    private sealed class Settings : IAiAgentRuntimeSettingsRepository
    {
        public IReadOnlyCollection<string> Keys {get;set;}=RealEstateAnalysisContextPolicy.ContextKeys;
        public bool Active {get;set;}=true;
        public int Calls {get;private set;}
        public Task<AiAgentRuntimeSettings> GetAsync(string key,string? companyId,CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AiAgentRuntimeSettings(key,Active,"openai","configured-model",null,"Fixture",1,null,Keys));
        }
    }
}
