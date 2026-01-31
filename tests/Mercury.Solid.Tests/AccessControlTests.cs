// AccessControlTests.cs
// Tests for Solid access control (WAC and ACP).

using SkyOmega.Mercury.Solid.AccessControl;
using Xunit;

namespace SkyOmega.Mercury.Solid.Tests;

public class AccessControlTests
{
    [Fact]
    public void AccessMode_RequiredForMethod_ReturnsCorrectModes()
    {
        Assert.Equal(AccessMode.Read, AccessModeExtensions.RequiredForMethod("GET"));
        Assert.Equal(AccessMode.Read, AccessModeExtensions.RequiredForMethod("HEAD"));
        Assert.Equal(AccessMode.Append, AccessModeExtensions.RequiredForMethod("POST"));
        Assert.Equal(AccessMode.Write, AccessModeExtensions.RequiredForMethod("PUT"));
        Assert.Equal(AccessMode.Write, AccessModeExtensions.RequiredForMethod("PATCH"));
        Assert.Equal(AccessMode.Write, AccessModeExtensions.RequiredForMethod("DELETE"));
        Assert.Equal(AccessMode.None, AccessModeExtensions.RequiredForMethod("OPTIONS"));
    }

    [Fact]
    public void AccessMode_ToWacIri_ReturnsCorrectIri()
    {
        Assert.Equal("http://www.w3.org/ns/auth/acl#Read", AccessMode.Read.ToWacIri());
        Assert.Equal("http://www.w3.org/ns/auth/acl#Write", AccessMode.Write.ToWacIri());
        Assert.Equal("http://www.w3.org/ns/auth/acl#Append", AccessMode.Append.ToWacIri());
        Assert.Equal("http://www.w3.org/ns/auth/acl#Control", AccessMode.Control.ToWacIri());
    }

    [Fact]
    public void AccessMode_FromWacIri_ParsesCorrectly()
    {
        Assert.Equal(AccessMode.Read, AccessModeExtensions.FromWacIri("http://www.w3.org/ns/auth/acl#Read"));
        Assert.Equal(AccessMode.Write, AccessModeExtensions.FromWacIri("http://www.w3.org/ns/auth/acl#Write"));
        Assert.Equal(AccessMode.Append, AccessModeExtensions.FromWacIri("http://www.w3.org/ns/auth/acl#Append"));
        Assert.Equal(AccessMode.Control, AccessModeExtensions.FromWacIri("http://www.w3.org/ns/auth/acl#Control"));
    }

    [Fact]
    public void AccessDecision_Allow_CreatesAllowDecision()
    {
        var decision = AccessDecision.Allow(AccessMode.Read | AccessMode.Write, "Test reason");

        Assert.True(decision.IsAllowed);
        Assert.Equal(AccessMode.Read | AccessMode.Write, decision.GrantedModes);
        Assert.Equal("Test reason", decision.Reason);
        Assert.True(decision.HasMode(AccessMode.Read));
        Assert.True(decision.HasMode(AccessMode.Write));
        Assert.False(decision.HasMode(AccessMode.Control));
    }

    [Fact]
    public void AccessDecision_Deny_CreatesDenyDecision()
    {
        var decision = AccessDecision.Deny("Access denied");

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessMode.None, decision.GrantedModes);
        Assert.Equal("Access denied", decision.Reason);
    }

    [Fact]
    public async Task AllowAllPolicy_AlwaysAllows()
    {
        var policy = AccessPolicyFactory.CreateAllowAll();

        var decision = await policy.EvaluateAsync(
            "https://alice.example.com/profile#me",
            "https://alice.example.com/data/resource",
            AccessMode.Read);

        Assert.True(decision.IsAllowed);
        Assert.Equal(AccessMode.All, decision.GrantedModes);
    }

    [Fact]
    public async Task AllowAllPolicy_AllowsUnauthenticated()
    {
        var policy = AccessPolicyFactory.CreateAllowAll();

        var decision = await policy.EvaluateAsync(
            null, // No WebID
            "https://example.com/public/resource",
            AccessMode.Read);

        Assert.True(decision.IsAllowed);
    }
}

public class WebAccessControlTests
{
    private class MockDocumentProvider : IAccessControlDocumentProvider
    {
        private readonly List<(string Subject, string Predicate, string Object)> _aclTriples;

        public MockDocumentProvider(List<(string Subject, string Predicate, string Object)> aclTriples)
        {
            _aclTriples = aclTriples;
        }

        public ValueTask<IReadOnlyList<(string Subject, string Predicate, string Object)>?> GetAccessControlDocumentAsync(
            string resourceUri, CancellationToken ct = default)
        {
            return ValueTask.FromResult<IReadOnlyList<(string Subject, string Predicate, string Object)>?>(_aclTriples);
        }

        public string? GetAccessControlDocumentUri(string resourceUri) => resourceUri + ".acl";

        public ValueTask<IReadOnlyList<(string Subject, string Predicate, string Object)>?> GetWebIdProfileAsync(
            string webId, CancellationToken ct = default)
        {
            return ValueTask.FromResult<IReadOnlyList<(string Subject, string Predicate, string Object)>?>(null);
        }
    }

    [Fact]
    public async Task WacPolicy_ExactAgentMatch_AllowsAccess()
    {
        // Arrange
        var aclTriples = new List<(string Subject, string Predicate, string Object)>
        {
            ("#auth", "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", "http://www.w3.org/ns/auth/acl#Authorization"),
            ("#auth", "http://www.w3.org/ns/auth/acl#agent", "<https://alice.example.com/profile#me>"),
            ("#auth", "http://www.w3.org/ns/auth/acl#accessTo", "<https://example.com/resource>"),
            ("#auth", "http://www.w3.org/ns/auth/acl#mode", "http://www.w3.org/ns/auth/acl#Read"),
            ("#auth", "http://www.w3.org/ns/auth/acl#mode", "http://www.w3.org/ns/auth/acl#Write")
        };

        var provider = new MockDocumentProvider(aclTriples);
        var policy = new WebAccessControl(provider);

        // Act
        var decision = await policy.EvaluateAsync(
            "https://alice.example.com/profile#me",
            "https://example.com/resource",
            AccessMode.Read);

        // Assert
        Assert.True(decision.IsAllowed);
        Assert.True(decision.HasMode(AccessMode.Read));
    }

    [Fact]
    public async Task WacPolicy_FoafAgent_AllowsAnyone()
    {
        // Arrange
        var aclTriples = new List<(string Subject, string Predicate, string Object)>
        {
            ("#public", "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", "http://www.w3.org/ns/auth/acl#Authorization"),
            ("#public", "http://www.w3.org/ns/auth/acl#agentClass", "http://xmlns.com/foaf/0.1/Agent"),
            ("#public", "http://www.w3.org/ns/auth/acl#accessTo", "<https://example.com/public>"),
            ("#public", "http://www.w3.org/ns/auth/acl#mode", "http://www.w3.org/ns/auth/acl#Read")
        };

        var provider = new MockDocumentProvider(aclTriples);
        var policy = new WebAccessControl(provider);

        // Act - unauthenticated access
        var decision = await policy.EvaluateAsync(
            null,
            "https://example.com/public",
            AccessMode.Read);

        // Assert
        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task WacPolicy_AuthenticatedAgent_RequiresAuth()
    {
        // Arrange
        var aclTriples = new List<(string Subject, string Predicate, string Object)>
        {
            ("#auth", "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", "http://www.w3.org/ns/auth/acl#Authorization"),
            ("#auth", "http://www.w3.org/ns/auth/acl#agentClass", "http://www.w3.org/ns/auth/acl#AuthenticatedAgent"),
            ("#auth", "http://www.w3.org/ns/auth/acl#accessTo", "<https://example.com/members>"),
            ("#auth", "http://www.w3.org/ns/auth/acl#mode", "http://www.w3.org/ns/auth/acl#Read")
        };

        var provider = new MockDocumentProvider(aclTriples);
        var policy = new WebAccessControl(provider);

        // Act - authenticated access
        var authedDecision = await policy.EvaluateAsync(
            "https://bob.example.com/profile#me",
            "https://example.com/members",
            AccessMode.Read);

        // Act - unauthenticated access
        var unauthedDecision = await policy.EvaluateAsync(
            null,
            "https://example.com/members",
            AccessMode.Read);

        // Assert
        Assert.True(authedDecision.IsAllowed);
        Assert.False(unauthedDecision.IsAllowed);
    }

    [Fact]
    public async Task WacPolicy_NoMatchingAuth_DeniesAccess()
    {
        // Arrange
        var aclTriples = new List<(string Subject, string Predicate, string Object)>
        {
            ("#auth", "http://www.w3.org/1999/02/22-rdf-syntax-ns#type", "http://www.w3.org/ns/auth/acl#Authorization"),
            ("#auth", "http://www.w3.org/ns/auth/acl#agent", "<https://alice.example.com/profile#me>"),
            ("#auth", "http://www.w3.org/ns/auth/acl#accessTo", "<https://example.com/private>"),
            ("#auth", "http://www.w3.org/ns/auth/acl#mode", "http://www.w3.org/ns/auth/acl#Read")
        };

        var provider = new MockDocumentProvider(aclTriples);
        var policy = new WebAccessControl(provider);

        // Act - different user
        var decision = await policy.EvaluateAsync(
            "https://bob.example.com/profile#me",
            "https://example.com/private",
            AccessMode.Read);

        // Assert
        Assert.False(decision.IsAllowed);
    }
}
