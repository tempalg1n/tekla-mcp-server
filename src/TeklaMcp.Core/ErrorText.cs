using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace TeklaMcp.Core;

/// <summary>
/// Turns an exception into a message that keeps the ACTUAL cause visible.
///
/// The Tekla write path surfaced two feedback reports whose real errors were hidden behind
/// wrapper exceptions: reflection produced «Адресат вызова создал исключение»
/// (TargetInvocationException) and a failed static ctor produced «Инициализатор типа …
/// выдал исключение» (TypeInitializationException) — both localized wrapper texts with the
/// diagnosis living in InnerException. Never report <c>ex.Message</c> of a wrapper.
/// </summary>
public static class ErrorText
{
    /// <summary>
    /// Flattens an exception chain into one line: wrapper layers are reduced to short type
    /// tags and every distinct inner message is kept, e.g.
    /// <c>TypeInitializationException(Tekla.Structures.ModuleManager) → RemotingException:
    /// Remote connection to Tekla Structures channel … failed</c>.
    /// </summary>
    public static string Flatten(Exception? exception)
    {
        if (exception is null) return "";
        var parts = new List<string>();
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;

        for (var current = exception; current != null && depth < 10; current = current.InnerException, depth++)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 1)
            {
                parts.Add("AggregateException(" + aggregate.InnerExceptions.Count + " errors)");
                for (var i = 0; i < aggregate.InnerExceptions.Count && i < 3; i++)
                    parts.Add(Flatten(aggregate.InnerExceptions[i]));
                if (aggregate.InnerExceptions.Count > 3) parts.Add("…");
                break;
            }

            if (IsWrapper(current))
            {
                // The wrapper's own message is localized boilerplate — keep only the type tag
                // (plus the failed type's name, the one genuinely useful bit).
                parts.Add(current is TypeInitializationException typeInit
                    ? "TypeInitializationException(" + typeInit.TypeName + ")"
                    : current.GetType().Name);
                continue;
            }

            var message = (current.Message ?? "").Trim();
            if (message.Length == 0) message = "(no message)";
            if (!seenMessages.Add(message)) continue; // some chains repeat the same text
            parts.Add(current.GetType().Name + ": " + message);
        }

        var text = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            if (text.Length > 0) text.Append(" → ");
            text.Append(part);
        }
        return text.Length == 0 ? exception.GetType().Name : text.ToString();
    }

    private static bool IsWrapper(Exception exception) =>
        exception.InnerException != null &&
        (exception is TargetInvocationException || exception is TypeInitializationException);
}
