namespace AudioPanel.Models;

/// <summary>
/// Explicit endpoint IDs for the normal and communication roles.
/// NormalEndpointId is intended to be applied to Console and Multimedia
/// unless the service chooses to honor the two role-specific IDs separately.
/// </summary>
public sealed class AudioEndpointSelection
{
    public string? NormalEndpointId { get; set; }

    public string? CommunicationsEndpointId { get; set; }

    public bool FollowNormalForCommunications { get; set; } = true;

    public string? GetEndpointId(AudioEndpointRole role)
    {
        return role == AudioEndpointRole.Communications &&
               !FollowNormalForCommunications
            ? CommunicationsEndpointId
            : NormalEndpointId;
    }

    public AudioEndpointSelection Clone()
    {
        return new AudioEndpointSelection
        {
            NormalEndpointId = NormalEndpointId,
            CommunicationsEndpointId = CommunicationsEndpointId,
            FollowNormalForCommunications = FollowNormalForCommunications
        };
    }
}
