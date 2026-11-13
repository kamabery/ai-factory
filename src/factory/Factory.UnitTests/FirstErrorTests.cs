using Factory.Core;

namespace Factory.UnitTests;

/// <summary>deploy.ps1's First-Error, case for case with factory.Tests.ps1's 'First-Error' block, plus HCP's structured log.</summary>
public class FirstErrorTests
{
    private const char E = (char)27;

    // GitHub prints a step's script in cyan before running it; J-019's attempt 3 flag quoted this echo.
    private static readonly string Echo = $"2026-10-24T17:00:55.1988402Z {E}[36;1m  echo \"::error::environment must be dev, staging or j<NNN>; got '$TF_ENV'\"{E}[0m";

    private static string Log(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void Skips_a_step_script_echoed_by_GitHub_and_finds_the_Terraform_diagnostic() =>
        Assert.Equal("Error: unable to build authorizer for Resource Manager API: could not configure AzureCli Authorizer", FirstError.Of(Log(
            Echo,
            $"2026-10-24T17:01:57.2015297Z {E}[31m│{E}[0m {E}[0m{E}[1m{E}[31mError: {E}[0m{E}[0m{E}[1munable to build authorizer for Resource Manager API: could not configure AzureCli Authorizer{E}[0m",
            "2026-10-24T17:02:07.3340748Z ##[error]Process completed with exit code 1.")));

    [Fact]
    public void Takes_an_actions_error_line_without_its_timestamp_and_marker() =>
        Assert.Equal("AADSTS700213: No matching federated identity record found for presented assertion subject 'repo:o@1/n@2:environment:preview'.", FirstError.Of(Log(
            Echo,
            "2026-10-24T13:24:01.9992159Z ##[error]AADSTS700213: No matching federated identity record found for presented assertion subject 'repo:o@1/n@2:environment:preview'.",
            "2026-10-24T13:24:02.1060520Z ##[error]Login failed with Error: The process '/usr/bin/az' failed with exit code 1.")));

    [Fact]
    public void Ignores_the_word_error_in_a_Terraform_warning_and_finds_the_failing_steps_own_output() =>
        Assert.Equal("Npgsql.PostgresException (0x80004005): 42501: permission denied for schema public", FirstError.Of(Log(
            $"2026-11-06T21:32:25.8579910Z {E}[33m│{E}[0m {E}[0mexceptional situations such as recovering from errors or mistakes, or when",
            $"2026-11-06T21:32:25.8580584Z {E}[33m│{E}[0m {E}[0mTerraform specifically suggests to use it as part of an error message.",
            $"2026-11-06T21:37:43.4937425Z {E}[36;1mpsql -v ON_ERROR_STOP=1 -q -v role=\"$DB_ROLE\"{E}[0m",
            "2026-11-06T21:38:03.0000000Z ##[group]Run dotnet ef database update",
            "2026-11-06T21:38:03.8921342Z Npgsql.PostgresException (0x80004005): 42501: permission denied for schema public",
            "2026-11-06T21:38:03.9295807Z     Severity: ERROR",
            "2026-11-06T21:38:03.9441792Z 42501: permission denied for schema public",
            "2026-11-06T21:38:03.9442112Z ",
            "2026-11-06T21:38:03.9442298Z POSITION: 28",
            "2026-11-06T21:38:03.9652543Z ##[error]Process completed with exit code 1.")));

    [Fact]
    public void Ignores_a_Terraform_warning_box_with_no_colour_as_HCPs_logs_print_it() =>
        Assert.Equal("Plan: 3 to add", FirstError.Of(Log(
            "╷", "│ Warning: Applied changes may be incomplete", "│ Terraform specifically suggests to use it as part of an error message.", "╵", "Plan: 3 to add")));

    [Fact]
    public void Adds_the_response_to_a_generic_Terraform_summary_and_skips_the_with_and_on_lines() =>
        Assert.Equal("Error: Failed to execute action: RESPONSE 400: 400 Bad Request; ERROR CODE: AADB2C", FirstError.Of(Log(
            "2026-11-06T23:20:00.9577971Z │ Error: Failed to execute action",
            "2026-11-06T23:20:00.9578515Z │ ",
            "2026-11-06T23:20:00.9579001Z │   with msgraph_resource_action.user_flow_link,",
            "2026-11-06T23:20:00.9579788Z │   on auth.entra-external.tf line 161, in resource \"msgraph_resource_action\" \"user_flow_link\":",
            "2026-11-06T23:20:00.9581143Z │ ",
            "2026-11-06T23:20:00.9581538Z │ API call failed: POST",
            "2026-11-06T23:20:00.9582860Z │ https://graph.microsoft.com/v1.0/identity/authenticationEventsFlows/1/conditions/applications/includeApplications",
            "2026-11-06T23:20:00.9584583Z │ RESPONSE 400: 400 Bad Request",
            "2026-11-06T23:20:00.9585033Z │ ERROR CODE: AADB2C",
            "2026-11-06T23:20:00.9586139Z │ {",
            "2026-11-06T23:20:01.0000000Z ╵")));

    [Fact]
    public void Joins_a_summary_that_azurerm_wrapped_over_several_lines() =>
        Assert.Equal("Error: creating Key Vault (Subscription: \"s-1\" Resource Group Name: \"rg-todoapp-j019\" Key Vault Name: \"kv-1\"): polling after CreateOrUpdate: unexpected status 404 (404 Not Found) with error: ResourceNotFound: The Resource was not found",
            FirstError.Of(Log(
                "│ Error: creating Key Vault (Subscription: \"s-1\"",
                "│ Resource Group Name: \"rg-todoapp-j019\"",
                "│ Key Vault Name: \"kv-1\"): polling after CreateOrUpdate: unexpected status 404 (404 Not Found) with error: ResourceNotFound: The Resource was not found",
                "│ ",
                "│   with azurerm_key_vault.main,",
                "╵")));

    [Fact]
    public void Adds_the_Message_when_the_summary_ends_by_promising_the_error() =>
        Assert.Equal("Error: creating Flexible Server (Subscription: \"s-1\" Flexible Server Name: \"psql-1\"): polling failed: the Azure API returned the following error: Message: \"Specified SKU is not supported in this region. Please choose a different SKU.\"",
            FirstError.Of(Log(
                "│ Error: creating Flexible Server (Subscription: \"s-1\"",
                "│ Flexible Server Name: \"psql-1\"): polling failed: the Azure API returned the following error:",
                "│ ",
                "│ Status: \"SkuNotAvailable\"",
                "│ Message: \"Specified SKU is not supported in this region. Please choose a different SKU.\"",
                "╵")));

    [Fact]
    public void Adds_the_wrapped_install_paragraph_to_a_provider_install_failure() =>
        Assert.Equal("Error: Failed to install provider: Error while installing azure/azapi v2.12.0: could not query provider registry for registry.terraform.io/azure/azapi: failed to retrieve authentication checksums for provider: the request failed after 2 attempts, please try again later: Get context deadline exceeded (Client.Timeout exceeded while awaiting headers)",
            FirstError.Of(Log(
                $"2026-11-08T11:59:34.9384951Z {E}[31m╷{E}[0m{E}[0m",
                $"2026-11-08T11:59:34.9385901Z {E}[31m│{E}[0m {E}[0m{E}[1m{E}[31mError: {E}[0m{E}[0m{E}[1mFailed to install provider{E}[0m",
                $"2026-11-08T11:59:34.9386293Z {E}[31m│{E}[0m {E}[0m",
                $"2026-11-08T11:59:34.9386752Z {E}[31m│{E}[0m {E}[0m{E}[0mError while installing azure/azapi v2.12.0: could not query provider",
                $"2026-11-08T11:59:34.9387351Z {E}[31m│{E}[0m {E}[0mregistry for registry.terraform.io/azure/azapi: failed to retrieve",
                $"2026-11-08T11:59:34.9388015Z {E}[31m│{E}[0m {E}[0mauthentication checksums for provider: the request failed after 2 attempts,",
                $"2026-11-08T11:59:34.9388484Z {E}[31m│{E}[0m {E}[0mplease try again later: Get",
                $"2026-11-08T11:59:34.9389964Z {E}[31m│{E}[0m {E}[0mcontext deadline exceeded (Client.Timeout exceeded while awaiting headers)",
                $"2026-11-08T11:59:34.9390395Z {E}[31m╵{E}[0m{E}[0m",
                "2026-11-08T11:59:34.9411801Z ##[error]Process completed with exit code 1.")));

    [Fact]
    public void Falls_back_to_the_generic_exit_line_when_nothing_better_is_there() =>
        Assert.Equal("Process completed with exit code 1.", FirstError.Of("deploy\n2026-10-24T17:02:07.3340748Z ##[error]Process completed with exit code 1."));

    [Fact]
    public void Reads_only_the_errors_of_HCPs_structured_log_as_Terraforms_own() =>
        Assert.Equal("Error: Invalid value for variable", FirstError.Of(Log(
            """{"@level":"info","@message":"Terraform 1.13.4"}""",
            """{"@level":"error","@message":"Error: Invalid value for variable"}""",
            """{"@level":"error" not json""")));

    [Fact]
    public void A_step_group_with_no_error_word_gives_its_last_line() =>
        Assert.Equal("exit status 2", FirstError.Of(Log("##[group]Run make", "building", "exit status 2", "##[error]Process completed with exit code 2.")));

    [Fact]
    public void A_group_just_before_the_exit_takes_the_exit_line_itself_as_PowerShells_descending_range_does() =>
        Assert.Equal("##[error]Process completed with exit code 1.", FirstError.Of(Log("x", "##[group]Run y", "##[error]Process completed with exit code 1.")));

    [Fact]
    public void Otherwise_takes_the_first_line_with_the_word_error_or_the_last_line()
    {
        Assert.Equal("an error happened", FirstError.Of("ok\nan error happened\nfine"));
        Assert.Equal("fine", FirstError.Of("ok\nfine\n"));
        Assert.Equal("", FirstError.Of(null));
    }
}
