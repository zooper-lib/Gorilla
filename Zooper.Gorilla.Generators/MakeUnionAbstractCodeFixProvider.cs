using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zooper.Gorilla.Generators;

/// <summary>
/// Fixes ZGOR003 by rewriting the declaration's modifiers: <c>sealed</c> becomes <c>abstract</c>,
/// and a declaration with neither gains <c>abstract</c>. Everything else — accessibility,
/// <c>partial</c>, and their order — is left alone. Registering a fix is also what gives
/// "Fix all occurrences in solution", which is how the migration edit is meant to be applied.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MakeUnionAbstractCodeFixProvider)), Shared]
public sealed class MakeUnionAbstractCodeFixProvider : CodeFixProvider
{
	private const string Title = "Make discriminated union abstract";

	public override ImmutableArray<string> FixableDiagnosticIds { get; } =
		ImmutableArray.Create(DiscriminatedUnionGenerator.NonAbstractUnionDiagnosticId);

	public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

	public override async Task RegisterCodeFixesAsync(CodeFixContext context)
	{
		var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

		if (root is null)
		{
			return;
		}

		foreach (var diagnostic in context.Diagnostics)
		{
			var declaration = root.FindToken(diagnostic.Location.SourceSpan.Start)
				.Parent?
				.AncestorsAndSelf()
				.OfType<TypeDeclarationSyntax>()
				.FirstOrDefault();

			if (declaration is null)
			{
				continue;
			}

			context.RegisterCodeFix(
				CodeAction.Create(
					Title,
					_ => Task.FromResult(context.Document.WithSyntaxRoot(
						root.ReplaceNode(declaration, MakeAbstract(declaration)))),
					equivalenceKey: Title),
				diagnostic);
		}
	}

	private static TypeDeclarationSyntax MakeAbstract(TypeDeclarationSyntax declaration)
	{
		var modifiers = declaration.Modifiers;

		if (modifiers.Any(SyntaxKind.AbstractKeyword))
		{
			return declaration;
		}

		var abstractKeyword = SyntaxFactory.Token(SyntaxKind.AbstractKeyword);
		var sealedIndex = modifiers.IndexOf(SyntaxKind.SealedKeyword);

		if (sealedIndex >= 0)
		{
			// Swap in place so the surrounding modifier order and trivia are untouched.
			var sealedKeyword = modifiers[sealedIndex];
			return declaration.WithModifiers(
				modifiers.Replace(sealedKeyword, abstractKeyword.WithTriviaFrom(sealedKeyword)));
		}

		// No sealed to replace: insert before `partial`, or at the end of the modifier list.
		var insertIndex = modifiers.IndexOf(SyntaxKind.PartialKeyword);
		if (insertIndex < 0)
		{
			insertIndex = modifiers.Count;
		}

		if (insertIndex == 0 && modifiers.Count > 0)
		{
			// The first modifier owns the declaration's leading trivia (indentation, doc
			// comments); hand it to the token taking its place.
			var first = modifiers[0];
			modifiers = modifiers.Replace(first, first.WithLeadingTrivia(SyntaxFactory.Space));
			abstractKeyword = abstractKeyword.WithLeadingTrivia(first.LeadingTrivia);
		}
		else
		{
			abstractKeyword = abstractKeyword.WithTrailingTrivia(SyntaxFactory.Space);
		}

		return declaration.WithModifiers(modifiers.Insert(insertIndex, abstractKeyword));
	}
}
