using System;
using System.Reflection;
using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// The field reports that motivated ErrorText: reflection wrappers («Адресат вызова создал
/// исключение») and failed static ctors («Инициализатор типа … выдал исключение») hid the
/// real cause. Flatten must always surface the innermost diagnosis.
/// </summary>
public class ErrorTextTests
{
    [Fact]
    public void Flatten_PlainException_KeepsTypeAndMessage()
    {
        var text = ErrorText.Flatten(new InvalidOperationException("no connection"));
        Assert.Equal("InvalidOperationException: no connection", text);
    }

    [Fact]
    public void Flatten_TargetInvocation_UnwrapsToInnerCause()
    {
        var wrapped = new TargetInvocationException(
            new InvalidOperationException("channel Tekla.Structures-:2023.0.0.0 failed"));
        var text = ErrorText.Flatten(wrapped);
        Assert.Contains("TargetInvocationException", text);
        Assert.Contains("channel Tekla.Structures-:2023.0.0.0 failed", text);
        // The wrapper's own localized boilerplate must NOT hide the cause.
        Assert.DoesNotContain(wrapped.Message, text);
    }

    [Fact]
    public void Flatten_TypeInitialization_NamesTheFailedTypeAndInnerCause()
    {
        var ex = new TypeInitializationException(
            "Tekla.Structures.ModuleManager",
            new InvalidOperationException("remote connection failed"));
        var text = ErrorText.Flatten(ex);
        Assert.Contains("TypeInitializationException(Tekla.Structures.ModuleManager)", text);
        Assert.Contains("remote connection failed", text);
    }

    [Fact]
    public void Flatten_NestedWrappers_KeepsWholeChain()
    {
        var ex = new TargetInvocationException(
            new TypeInitializationException(
                "Tekla.Structures.ModuleManager",
                new InvalidOperationException("pipe not found")));
        var text = ErrorText.Flatten(ex);
        Assert.Contains("TargetInvocationException", text);
        Assert.Contains("ModuleManager", text);
        Assert.Contains("pipe not found", text);
    }

    [Fact]
    public void Flatten_Aggregate_ListsInnerErrors()
    {
        var ex = new AggregateException(
            new InvalidOperationException("first"),
            new InvalidOperationException("second"));
        var text = ErrorText.Flatten(ex);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Flatten_RepeatedMessages_Deduplicated()
    {
        var ex = new InvalidOperationException("same text",
            new InvalidOperationException("same text"));
        var text = ErrorText.Flatten(ex);
        Assert.Equal("InvalidOperationException: same text", text);
    }

    [Fact]
    public void Flatten_Null_ReturnsEmpty()
    {
        Assert.Equal("", ErrorText.Flatten(null));
    }

    [Fact]
    public void Flatten_WrapperWithoutInner_FallsBackToTypeName()
    {
        // A TypeInitializationException with no inner is unusual but must not crash.
        var text = ErrorText.Flatten(new TypeInitializationException("Some.Type", null));
        Assert.Contains("Some.Type", text);
    }
}
