using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AdysTech.CredentialManager;
using WixToolset.Dtf.WindowsInstaller;

namespace JenkinsAsService.CustomActions;

public class SecretActions
{
    [CustomAction]
    public static ActionResult WriteConfig(Session session)
    {
        try
        {
            var installDir = session.CustomActionData["INSTALLFOLDER"];
            var jenkinsUrl = session.CustomActionData["JENKINS_URL"];
            var secret = session.CustomActionData["JENKINS_SECRET"];
            var agentName = session.CustomActionData["JENKINS_AGENT_NAME"];
            var javaPath = session.CustomActionData["JENKINS_JAVA_PATH"];
            var secretMode = session.CustomActionData["JENKINS_SECRET_MODE"];

            string configSecret;
            switch (secretMode)
            {
                case "Dpapi":
                    var plainBytes = Encoding.UTF8.GetBytes(secret);
                    var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.LocalMachine);
                    configSecret = Convert.ToBase64String(encrypted);
                    break;

                case "EnvironmentVariable":
                    const string envVarName = "JENKINS_AGENT_SECRET";
                    Environment.SetEnvironmentVariable(envVarName, secret, EnvironmentVariableTarget.Machine);
                    configSecret = envVarName;
                    session.Log($"Created system environment variable: {envVarName}");
                    break;

                case "CredentialManager":
                    const string targetName = "JenkinsAsService/AgentSecret";
                    CredentialManager.SaveCredentials(targetName, new NetworkCredential("JenkinsAgent", secret));
                    configSecret = targetName;
                    session.Log($"Stored credential in Windows Credential Manager: {targetName}");
                    break;

                case "Unprotected":
                    configSecret = secret;
                    break;

                default:
                    session.Log($"Unknown secret mode: {secretMode}. Falling back to Unprotected.");
                    configSecret = secret;
                    secretMode = "Unprotected";
                    break;
            }

            var json = $@"{{
  ""Jenkins"": {{
    ""JenkinsURL"": ""{EscapeJson(jenkinsUrl)}"",
    ""AgentSecret"": ""{EscapeJson(configSecret)}"",
    ""SecretMode"": ""{EscapeJson(secretMode)}"",
    ""AgentName"": ""{EscapeJson(agentName)}"",
    ""JavaPath"": ""{EscapeJson(javaPath)}"",
    ""CustomArguments"": """",
    ""DebugMode"": false,
    ""CompactLog"": false,
    ""MaxRetries"": 0
  }}
}}";

            var configPath = Path.Combine(installDir, "appsettings.json");
            File.WriteAllText(configPath, json, Encoding.UTF8);
            session.Log($"Wrote config to: {configPath} (SecretMode: {secretMode})");

            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log($"WriteConfig failed: {ex}");
            return ActionResult.Failure;
        }
    }

    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
