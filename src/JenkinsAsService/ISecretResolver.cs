namespace JenkinsAsService;

public interface ISecretResolver
{
    string Resolve(ServiceSettings settings);
}
