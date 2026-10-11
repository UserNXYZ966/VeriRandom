using CommunityToolkit.Mvvm.ComponentModel;
using SecRandom.Core.Enums.Configs;

namespace SecRandom.Core.Models.SubConfigs.General;

public partial class VerificationSettingsConfig : ObservableObject
{
    [ObservableProperty] private VerificationMode _mode = VerificationMode.Ordinary;

    // Ordinary-mode-only opt-in. When on, the ordinary proof seed comes from an external beacon pulse
    // instead of the local CSPRNG, so the operator cannot inject custom entropy into the draw.
    [ObservableProperty] private bool _beaconEntropyEnabled;

    // Off by default. When on, a draw may start fetching the beacon pulse a moment before it is needed (the
    // quick-draw rolling animation is the intended window) so the HTTP round trip hides behind the animation.
    // Only the *pulse* is prefetched: the per-pulse sequence is still reserved inside ReserveAsync, so a
    // prefetch whose draw never happens consumes no sequence and leaves no gap in the proof chain. A pulse
    // that ages past its period before the draw costs the usual half of the trust score, nothing more.
    [ObservableProperty] private bool _beaconPrefetchEnabled;

    // Advanced override. The default is the official NIST Beacon v2 base endpoint; a mirror is allowed
    // because the reference proof records the raw pulse and its signature, so a substituted pulse stays detectable.
    [ObservableProperty] private string _beaconEndpoint = "https://beacon.nist.gov/beacon/2.0/";

    // On by default. Turning it off stops both the Up and the reference chain from requesting an RFC 3161
    // token, which also removes the period anchor the beacon match is normally checked against: the
    // reference proof's recorded pulse index is then the only way back to the pulse. The settings page
    // warns about that coupling before it persists the change.
    [ObservableProperty] private bool _timestampAuthorityEnabled = true;

    // No default on purpose: first-run setup asks the user to choose between submitting an ordinary draw
    // for replay attestation and not submitting it, and an installation that has not been asked yet stays
    // Unset. Every reader must treat Unset as "do not upload".
    [ObservableProperty] private AttestationUploadMode _attestationUpload = AttestationUploadMode.Unset;
}
