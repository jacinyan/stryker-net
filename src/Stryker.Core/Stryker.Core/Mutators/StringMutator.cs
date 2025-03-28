using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stryker.Abstractions;
using Stryker.Core.Helpers;

namespace Stryker.Core.Mutators;

public class StringMutator : MutatorBase<LiteralExpressionSyntax>
{
    public override MutationLevel MutationLevel => MutationLevel.Standard;

    public override IEnumerable<Mutation> ApplyMutations(LiteralExpressionSyntax node, SemanticModel semanticModel)
    {
        // Get objectCreationSyntax to check if it contains a regex type.
        var root = node.Parent?.Parent?.Parent;

        if (!IsSpecialType(root) && node.IsAStringExpression())
        {
            var currentValue = (string)node.Token.Value;
            var replacementValue = currentValue == "" ? "Stryker was here!" : "";
            yield return new Mutation
            {
                OriginalNode = node,
                ReplacementNode = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(replacementValue)),
                DisplayName = "String mutation",
                Type = Mutator.String
            };
        }
    }

    private static bool IsSpecialType(SyntaxNode root) => root switch
    {
        ObjectCreationExpressionSyntax ctor => IsCtorOfType(ctor, typeof(Regex)) || IsCtorOfType(ctor, typeof(Guid)),
        _ => false
    };

    private static bool IsCtorOfType(ObjectCreationExpressionSyntax ctor, Type type)
    {
        var ctorType = ctor.Type.ToString();
        return ctorType == type.Name || ctorType == type.FullName;
    }
}
