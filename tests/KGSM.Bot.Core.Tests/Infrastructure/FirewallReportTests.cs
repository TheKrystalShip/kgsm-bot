using FluentAssertions;

using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.KGSM;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using Xunit;

namespace KGSM.Bot.Core.Tests.Infrastructure;

/// <summary>
/// "Can you actually reach it" is the half of the connection answer nothing else gives, and it is the
/// easiest one to get wrong in the direction that matters: reporting a firewall that is not filtering
/// as one that has everything closed. These pin every way of not knowing to a value that is not a
/// denial.
/// </summary>
public sealed class FirewallReportTests
{
    private static readonly IReadOnlyList<PortMapping> Wanted =
    [
        new() { Start = 25565, End = 25565, Protocol = "tcp" },
        new() { Start = 25565, End = 25565, Protocol = "udp" },
    ];

    private readonly IFirewallService _firewall = Substitute.For<IFirewallService>();

    private FirewallReport Report() => new(_firewall, NullLogger<FirewallReport>.Instance);

    private void Answers(FirewallListResult listed, string backend = "ufw")
    {
        _firewall.BackendAsync(Arg.Any<CancellationToken>())
            .Returns(new FirewallBackendInfo { Backend = backend, CanList = true });
        _firewall.ListOwnedAsync("minecraft", Arg.Any<CancellationToken>()).Returns(listed);
    }

    [Fact]
    public async Task EveryWantedPortHeldIsOpen()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Enforcing,
            Rules = [new FirewallOwnedRule("minecraft", Wanted)],
        });

        FirewallExposure exposure = await Report().DescribeAsync("minecraft", Wanted);

        exposure.Exposure.Should().Be(PortExposure.Open);
        exposure.Backend.Should().Be("ufw");
    }

    /// <summary>
    /// The authority is free to hold the same ports as a differently-shaped set of ranges, so the
    /// comparison is per port. A range-shape difference is not a hole.
    /// </summary>
    [Fact]
    public async Task ARangeCoveringTheWantedPortsIsOpen()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Enforcing,
            Rules =
            [
                new FirewallOwnedRule("minecraft",
                [
                    new() { Start = 25560, End = 25570, Protocol = "tcp" },
                    new() { Start = 25560, End = 25570, Protocol = "udp" },
                ]),
            ],
        });

        (await Report().DescribeAsync("minecraft", Wanted)).Exposure.Should().Be(PortExposure.Open);
    }

    [Fact]
    public async Task SomeOfTheWantedPortsHeldIsPartial()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Enforcing,
            Rules = [new FirewallOwnedRule("minecraft", [Wanted[0]])],
        });

        (await Report().DescribeAsync("minecraft", Wanted)).Exposure.Should().Be(PortExposure.Partial);
    }

    [Fact]
    public async Task NothingHeldByAnEnforcingBackendIsClosed()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Enforcing,
            Rules = [],
        });

        (await Report().DescribeAsync("minecraft", Wanted)).Exposure.Should().Be(PortExposure.Closed);
    }

    /// <summary>
    /// The one that matters. A backend that is installed but not enforcing filters nothing, so its
    /// empty rule set means every port is reachable — reading it as "closed" would tell somebody their
    /// server is unreachable at the exact moment it is reachable by anyone.
    /// </summary>
    [Fact]
    public async Task AnInactiveBackendIsUnfilteredAndNeverClosed()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Inactive,
            Rules = [],
        });

        (await Report().DescribeAsync("minecraft", Wanted)).Exposure
            .Should().Be(PortExposure.Unfiltered);
    }

    [Fact]
    public async Task AnUnenumerableBackendIsUnknownAndNeverClosed()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Unknown,
            Enforcement = FirewallEnforcement.Enforcing,
            Rules = [],
        });

        (await Report().DescribeAsync("minecraft", Wanted)).Exposure.Should().Be(PortExposure.Unknown);
    }

    /// <summary>
    /// A pre-1.1.0 authority reports no enforcement at all. Not knowing whether anything is filtering
    /// makes the rule set uninterpretable, whatever it contains.
    /// </summary>
    [Fact]
    public async Task UnknownEnforcementIsUnknownEvenWithRules()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Unknown,
            Rules = [new FirewallOwnedRule("minecraft", Wanted)],
        });

        (await Report().DescribeAsync("minecraft", Wanted)).Exposure.Should().Be(PortExposure.Unknown);
    }

    /// <summary>
    /// The firewall is an optional sibling. An unreachable one costs this one answer and nothing else
    /// — it is not an error, and it is certainly not a denial.
    /// </summary>
    [Fact]
    public async Task AnUnreachableAuthorityIsUnavailable()
    {
        _firewall.BackendAsync(Arg.Any<CancellationToken>())
            .Returns<FirewallBackendInfo>(_ => throw new IOException("no such socket"));

        FirewallExposure exposure = await Report().DescribeAsync("minecraft", Wanted);

        exposure.Exposure.Should().Be(PortExposure.Unavailable);
        exposure.Backend.Should().BeNull();
    }

    /// <summary>
    /// A server that declares no ports has nothing to be open or closed about. Calling that "closed"
    /// would read as a problem where there is not one.
    /// </summary>
    [Fact]
    public async Task AServerWithNoPortsIsNotReportedAsClosed()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Enforcing,
            Rules = [],
        });

        (await Report().DescribeAsync("minecraft", [])).Exposure.Should().Be(PortExposure.Unknown);
    }

    /// <summary>
    /// Rules the authority holds for a different server say nothing about this one.
    /// </summary>
    [Fact]
    public async Task AnotherServersRulesDoNotCountAsThisOnes()
    {
        Answers(new FirewallListResult
        {
            Status = FirewallListStatus.Ok,
            Enforcement = FirewallEnforcement.Enforcing,
            Rules = [new FirewallOwnedRule("factorio", Wanted)],
        });

        (await Report().DescribeAsync("minecraft", Wanted)).Exposure.Should().Be(PortExposure.Closed);
    }
}
