using System;
using System.Linq;
using System.Runtime.Versioning;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.FrameworkSmoke;

public class RuntimeTargetTests
{
    [Fact]
    public void Executes_the_real_matching_core_target()
    {
        var core = typeof(JsonValue).Assembly;
        var target = core.GetCustomAttributes(typeof(TargetFrameworkAttribute), false)
            .Cast<TargetFrameworkAttribute>().Single().FrameworkName;
#if NETFRAMEWORK
        Assert.Equal(".NETFramework,Version=v4.8", target);
        Assert.Equal(4, Environment.Version.Major);
#else
        Assert.Equal(".NETCoreApp,Version=v7.0", target);
        Assert.Equal(7, Environment.Version.Major);
#endif
        Assert.False(string.IsNullOrWhiteSpace(core.Location));
    }
}
