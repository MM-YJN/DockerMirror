using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

[OptionsValidator]
internal sealed partial class MirrorOptionsValidator : IValidateOptions<MirrorOptions>
{
}
