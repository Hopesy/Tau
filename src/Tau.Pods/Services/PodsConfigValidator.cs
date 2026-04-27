using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodsConfigValidator
{
    public IReadOnlyList<string> Validate(PodsConfig config)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pod in config.Pods)
        {
            if (string.IsNullOrWhiteSpace(pod.Id))
            {
                errors.Add("Pod id is required.");
            }
            else if (!ids.Add(pod.Id))
            {
                errors.Add($"Duplicate pod id: {pod.Id}");
            }

            if (string.IsNullOrWhiteSpace(pod.Provider)) errors.Add($"Pod {pod.Id}: provider is required.");
            if (string.IsNullOrWhiteSpace(pod.Model)) errors.Add($"Pod {pod.Id}: model is required.");
            if (string.IsNullOrWhiteSpace(pod.Region)) errors.Add($"Pod {pod.Id}: region is required.");
            if (string.IsNullOrWhiteSpace(pod.Endpoint) && string.IsNullOrWhiteSpace(pod.SshHost)) errors.Add($"Pod {pod.Id}: either endpoint or sshHost must be configured.");
            if (pod.SshPort is < 1 or > 65535) errors.Add($"Pod {pod.Id}: sshPort must be between 1 and 65535.");
        }

        return errors;
    }
}
