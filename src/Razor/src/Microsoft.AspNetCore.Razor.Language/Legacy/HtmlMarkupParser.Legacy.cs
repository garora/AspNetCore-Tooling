// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Syntax.InternalSyntax;

namespace Microsoft.AspNetCore.Razor.Language.Legacy
{
    internal partial class HtmlMarkupParser : TokenizerBackedParser<HtmlTokenizer>
    {
        private SourceLocation _lastTagStart = SourceLocation.Zero;
        private SyntaxToken _bufferedOpenAngle;

        private bool CaseSensitive { get; set; }

        private StringComparison Comparison
        {
            get { return CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; }
        }

        // Special tags include <!--, <!DOCTYPE, <![CDATA and <? tags
        private bool AtSpecialTag
        {
            get
            {
                if (At(SyntaxKind.OpenAngle))
                {
                    if (NextIs(SyntaxKind.Bang))
                    {
                        return !IsBangEscape(lookahead: 1);
                    }

                    return NextIs(SyntaxKind.QuestionMark);
                }

                return false;
            }
        }

        private void SkipToAndParseCode(in SyntaxListBuilder<RazorSyntaxNode> builder, SyntaxKind type)
        {
            SkipToAndParseCode(builder, token => token.Kind == type);
        }

        private void SkipToAndParseCode(in SyntaxListBuilder<RazorSyntaxNode> builder, Func<SyntaxToken, bool> condition)
        {
            SyntaxToken last = null;
            var startOfLine = false;
            while (!EndOfFile && !condition(CurrentToken))
            {
                if (Context.NullGenerateWhitespaceAndNewLine)
                {
                    Context.NullGenerateWhitespaceAndNewLine = false;
                    SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                    AcceptWhile(token => token.Kind == SyntaxKind.Whitespace);
                    if (At(SyntaxKind.NewLine))
                    {
                        AcceptAndMoveNext();
                    }

                    builder.Add(OutputAsMarkupEphemeralLiteral());
                }
                else if (At(SyntaxKind.NewLine))
                {
                    if (last != null)
                    {
                        Accept(last);
                    }

                    // Mark the start of a new line
                    startOfLine = true;
                    last = null;
                    AcceptAndMoveNext();
                }
                else if (At(SyntaxKind.Transition))
                {
                    var transition = CurrentToken;
                    NextToken();
                    if (At(SyntaxKind.Transition))
                    {
                        if (last != null)
                        {
                            Accept(last);
                            last = null;
                        }
                        builder.Add(OutputAsMarkupLiteral());
                        Accept(transition);
                        SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                        builder.Add(OutputAsMarkupEphemeralLiteral());
                        AcceptAndMoveNext();
                        continue; // while
                    }
                    else
                    {
                        if (!EndOfFile)
                        {
                            PutCurrentBack();
                        }
                        PutBack(transition);
                    }

                    // Handle whitespace rewriting
                    if (last != null)
                    {
                        if (!Context.DesignTimeMode && last.Kind == SyntaxKind.Whitespace && startOfLine)
                        {
                            // Put the whitespace back too
                            startOfLine = false;
                            PutBack(last);
                            last = null;
                        }
                        else
                        {
                            // Accept last
                            Accept(last);
                            last = null;
                        }
                    }

                    OtherParserBlock(builder);
                }
                else if (At(SyntaxKind.RazorCommentTransition))
                {
                    var shouldRenderWhitespace = true;
                    if (last != null)
                    {
                        // Don't render the whitespace between the start of the line and the razor comment.
                        if (startOfLine && last.Kind == SyntaxKind.Whitespace)
                        {
                            AcceptMarkerTokenIfNecessary();
                            // Output the tokens that may have been accepted prior to the whitespace.
                            builder.Add(OutputAsMarkupLiteral());

                            SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                            shouldRenderWhitespace = false;
                        }

                        Accept(last);
                        last = null;
                    }

                    AcceptMarkerTokenIfNecessary();
                    if (shouldRenderWhitespace)
                    {
                        builder.Add(OutputAsMarkupLiteral());
                    }
                    else
                    {
                        builder.Add(OutputAsMarkupEphemeralLiteral());
                    }

                    var comment = ParseRazorComment();
                    builder.Add(comment);

                    // Handle the whitespace and newline at the end of a razor comment.
                    if (startOfLine &&
                        (At(SyntaxKind.NewLine) ||
                        (At(SyntaxKind.Whitespace) && NextIs(SyntaxKind.NewLine))))
                    {
                        AcceptWhile(IsSpacingToken(includeNewLines: false));
                        AcceptAndMoveNext();
                        SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                        builder.Add(OutputAsMarkupEphemeralLiteral());
                    }
                }
                else
                {
                    // As long as we see whitespace, we're still at the "start" of the line
                    startOfLine &= At(SyntaxKind.Whitespace);

                    // If there's a last token, accept it
                    if (last != null)
                    {
                        Accept(last);
                        last = null;
                    }

                    // Advance
                    last = CurrentToken;
                    NextToken();
                }
            }

            if (last != null)
            {
                Accept(last);
            }
        }

        /// <summary>
        /// Reads the content of a tag (if present) in the MarkupDocument (or MarkupSection) context,
        /// where we don't care about maintaining a stack of tags.
        /// </summary>
        private void ParseTagInDocumentContext(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            if (At(SyntaxKind.OpenAngle))
            {
                if (NextIs(SyntaxKind.Bang))
                {
                    // Checking to see if we meet the conditions of a special '!' tag: <!DOCTYPE, <![CDATA[, <!--.
                    if (!IsBangEscape(lookahead: 1))
                    {
                        if (Lookahead(2)?.Kind == SyntaxKind.DoubleHyphen)
                        {
                            builder.Add(OutputAsMarkupLiteral());
                        }

                        AcceptAndMoveNext(); // Accept '<'
                        ParseBangTag(builder);

                        return;
                    }

                    // We should behave like a normal tag that has a parser escape, fall through to the normal
                    // tag logic.
                }
                else if (NextIs(SyntaxKind.QuestionMark))
                {
                    AcceptAndMoveNext(); // Accept '<'
                    TryParseXmlPI(builder);
                    return;
                }

                builder.Add(OutputAsMarkupLiteral());

                // Start tag block
                using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                {
                    var tagBuilder = pooledResult.Builder;
                    AcceptAndMoveNext(); // Accept '<'

                    if (!At(SyntaxKind.ForwardSlash))
                    {
                        TryParseBangEscape(tagBuilder);

                        // Parsing a start tag
                        var scriptTag = At(SyntaxKind.Text) &&
                                        string.Equals(CurrentToken.Content, "script", StringComparison.OrdinalIgnoreCase);
                        TryAccept(SyntaxKind.Text);
                        // Output open angle and tag name
                        tagBuilder.Add(OutputAsMarkupLiteral());
                        ParseAttributes(tagBuilder); // Parse the tag, don't care about the content
                        TryAccept(SyntaxKind.ForwardSlash);
                        TryAccept(SyntaxKind.CloseAngle);

                        // If the script tag expects javascript content then we should do minimal parsing until we reach
                        // the end script tag. Don't want to incorrectly parse a "var tag = '<input />';" as an HTML tag.
                        if (scriptTag && !CurrentScriptTagExpectsHtml(tagBuilder))
                        {
                            tagBuilder.Add(OutputAsMarkupLiteral());
                            var block = SyntaxFactory.MarkupTagBlock(tagBuilder.ToList());
                            builder.Add(block);

                            SkipToEndScriptAndParseCode(builder);
                            return;
                        }
                    }
                    else
                    {
                        // Parsing an end tag
                        // This section can accept things like: '</p  >' or '</p>' etc.
                        TryAccept(SyntaxKind.ForwardSlash);

                        // Whitespace here is invalid (according to the spec)
                        TryParseBangEscape(tagBuilder);
                        TryAccept(SyntaxKind.Text);
                        TryAccept(SyntaxKind.Whitespace);
                        TryAccept(SyntaxKind.CloseAngle);
                    }

                    tagBuilder.Add(OutputAsMarkupLiteral());

                    // End tag block
                    var tagBlock = SyntaxFactory.MarkupTagBlock(tagBuilder.ToList());
                    builder.Add(tagBlock);
                }
            }
        }

        private bool ParseBangTag(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            // Accept "!"
            Assert(SyntaxKind.Bang);

            if (AcceptAndMoveNext())
            {
                if (LegacyIsHtmlCommentAhead())
                {
                    using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                    {
                        var htmlCommentBuilder = pooledResult.Builder;

                        // Accept the double-hyphen token at the beginning of the comment block.
                        AcceptAndMoveNext();
                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                        htmlCommentBuilder.Add(OutputAsMarkupLiteral());

                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.Whitespace;
                        while (!EndOfFile)
                        {
                            SkipToAndParseCode(htmlCommentBuilder, SyntaxKind.DoubleHyphen);
                            var lastDoubleHyphen = AcceptAllButLastDoubleHyphens();

                            if (At(SyntaxKind.CloseAngle))
                            {
                                // Output the content in the comment block as a separate markup
                                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.Whitespace;
                                htmlCommentBuilder.Add(OutputAsMarkupLiteral());

                                // This is the end of a comment block
                                Accept(lastDoubleHyphen);
                                AcceptAndMoveNext();
                                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                                htmlCommentBuilder.Add(OutputAsMarkupLiteral());
                                var commentBlock = SyntaxFactory.MarkupCommentBlock(htmlCommentBuilder.ToList());
                                builder.Add(commentBlock);
                                return true;
                            }
                            else if (lastDoubleHyphen != null)
                            {
                                Accept(lastDoubleHyphen);
                            }
                        }
                    }
                }
                else if (CurrentToken.Kind == SyntaxKind.LeftBracket)
                {
                    if (AcceptAndMoveNext())
                    {
                        return TryParseCData(builder);
                    }
                }
                else
                {
                    AcceptAndMoveNext();
                    return LegacyAcceptTokenUntilAll(builder, SyntaxKind.CloseAngle);
                }
            }

            return false;
        }

        protected bool LegacyIsHtmlCommentAhead()
        {
            // From HTML5 Specification, available at http://www.w3.org/TR/html52/syntax.html#comments

            // Comments must have the following format:
            // 1. The string "<!--"
            // 2. Optionally, text, with the additional restriction that the text
            //      2.1 must not start with the string ">" nor start with the string "->"
            //      2.2 nor contain the strings
            //          2.2.1 "<!--"
            //          2.2.2 "-->" As we will be treating this as a comment ending, there is no need to handle this case at all.
            //          2.2.3 "--!>"
            //      2.3 nor end with the string "<!-".
            // 3. The string "-->"

            if (CurrentToken.Kind != SyntaxKind.DoubleHyphen)
            {
                return false;
            }

            // Check condition 2.1
            if (NextIs(SyntaxKind.CloseAngle) || NextIs(next => IsHyphen(next) && NextIs(SyntaxKind.CloseAngle)))
            {
                return false;
            }

            // Check condition 2.2
            var isValidComment = false;
            LookaheadUntil((token, prevTokens) =>
            {
                if (token.Kind == SyntaxKind.DoubleHyphen)
                {
                    if (NextIs(SyntaxKind.CloseAngle))
                    {
                        // Check condition 2.3: We're at the end of a comment. Check to make sure the text ending is allowed.
                        isValidComment = !IsCommentContentEndingInvalid(prevTokens);
                        return true;
                    }
                    else if (NextIs(ns => IsHyphen(ns) && NextIs(SyntaxKind.CloseAngle)))
                    {
                        // Check condition 2.3: we're at the end of a comment, which has an extra dash.
                        // Need to treat the dash as part of the content and check the ending.
                        // However, that case would have already been checked as part of check from 2.2.1 which
                        // would already fail this iteration and we wouldn't get here
                        isValidComment = true;
                        return true;
                    }
                    else if (NextIs(ns => ns.Kind == SyntaxKind.Bang && NextIs(SyntaxKind.CloseAngle)))
                    {
                        // This is condition 2.2.3
                        isValidComment = false;
                        return true;
                    }
                }
                else if (token.Kind == SyntaxKind.OpenAngle)
                {
                    // Checking condition 2.2.1
                    if (NextIs(ns => ns.Kind == SyntaxKind.Bang && NextIs(SyntaxKind.DoubleHyphen)))
                    {
                        isValidComment = false;
                        return true;
                    }
                }

                return false;
            });

            return isValidComment;
        }

        private bool TryParseCData(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            if (CurrentToken.Kind == SyntaxKind.Text && string.Equals(CurrentToken.Content, "cdata", StringComparison.OrdinalIgnoreCase))
            {
                if (AcceptAndMoveNext())
                {
                    if (CurrentToken.Kind == SyntaxKind.LeftBracket)
                    {
                        return LegacyAcceptTokenUntilAll(builder, SyntaxKind.RightBracket, SyntaxKind.RightBracket, SyntaxKind.CloseAngle);
                    }
                }
            }

            return false;
        }

        private bool TryParseXmlPI(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            // Accept "?"
            Assert(SyntaxKind.QuestionMark);
            AcceptAndMoveNext();
            return LegacyAcceptTokenUntilAll(builder, SyntaxKind.QuestionMark, SyntaxKind.CloseAngle);
        }

        private void SkipToEndScriptAndParseCode(in SyntaxListBuilder<RazorSyntaxNode> builder, AcceptedCharactersInternal endTagAcceptedCharacters = AcceptedCharactersInternal.Any)
        {
            // Special case for <script>: Skip to end of script tag and parse code
            var seenEndScript = false;

            while (!seenEndScript && !EndOfFile)
            {
                SkipToAndParseCode(builder, SyntaxKind.OpenAngle);
                var tagStart = CurrentStart;

                if (NextIs(SyntaxKind.ForwardSlash))
                {
                    var openAngle = CurrentToken;
                    NextToken(); // Skip over '<', current is '/'
                    var solidus = CurrentToken;
                    NextToken(); // Skip over '/', current should be text

                    if (At(SyntaxKind.Text) &&
                        string.Equals(CurrentToken.Content, ScriptTagName, StringComparison.OrdinalIgnoreCase))
                    {
                        seenEndScript = true;
                    }

                    // We put everything back because we just wanted to look ahead to see if the current end tag that we're parsing is
                    // the script tag.  If so we'll generate correct code to encompass it.
                    PutCurrentBack(); // Put back whatever was after the solidus
                    PutBack(solidus); // Put back '/'
                    PutBack(openAngle); // Put back '<'

                    // We just looked ahead, this NextToken will set CurrentToken to an open angle bracket.
                    NextToken();
                }

                if (seenEndScript)
                {
                    builder.Add(OutputAsMarkupLiteral());

                    using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                    {
                        var tagBuilder = pooledResult.Builder;
                        SpanContext.EditHandler.AcceptedCharacters = endTagAcceptedCharacters;

                        AcceptAndMoveNext(); // '<'
                        AcceptAndMoveNext(); // '/'
                        SkipToAndParseCode(tagBuilder, SyntaxKind.CloseAngle);
                        if (!TryAccept(SyntaxKind.CloseAngle))
                        {
                            Context.ErrorSink.OnError(
                                RazorDiagnosticFactory.CreateParsing_UnfinishedTag(
                                    new SourceSpan(SourceLocationTracker.Advance(tagStart, "</"), ScriptTagName.Length),
                                    ScriptTagName));
                            var closeAngle = SyntaxFactory.MissingToken(SyntaxKind.CloseAngle);
                            Accept(closeAngle);
                        }
                        tagBuilder.Add(OutputAsMarkupLiteral());
                        builder.Add(SyntaxFactory.MarkupTagBlock(tagBuilder.ToList()));
                    }
                }
                else
                {
                    AcceptAndMoveNext(); // Accept '<' (not the closing script tag's open angle)
                }
            }
        }

        private bool CurrentScriptTagExpectsHtml(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            Debug.Assert(!builder.IsNull);

            MarkupAttributeBlockSyntax typeAttribute = null;
            for (var i = 0; i < builder.Count; i++)
            {
                var node = builder[i];
                if (node.IsToken || node.IsTrivia)
                {
                    continue;
                }

                if (node is MarkupAttributeBlockSyntax attributeBlock &&
                    attributeBlock.Value.Children.Count > 0 &&
                    IsTypeAttribute(attributeBlock))
                {
                    typeAttribute = attributeBlock;
                    break;
                }
            }

            if (typeAttribute != null)
            {
                var contentValues = typeAttribute.Value.CreateRed().DescendantNodes().Where(n => n.IsToken).Cast<Syntax.SyntaxToken>();

                var scriptType = string.Concat(contentValues.Select(t => t.Content)).Trim();

                // Does not allow charset parameter (or any other parameters).
                return string.Equals(scriptType, "text/html", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private bool LegacyAcceptTokenUntilAll(in SyntaxListBuilder<RazorSyntaxNode> builder, params SyntaxKind[] endSequence)
        {
            while (!EndOfFile)
            {
                SkipToAndParseCode(builder, endSequence[0]);
                if (AcceptAll(endSequence))
                {
                    return true;
                }
            }
            Debug.Assert(EndOfFile);
            SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.Any;
            return false;
        }

        public MarkupBlockSyntax ParseBlock()
        {
            if (Context == null)
            {
                throw new InvalidOperationException(Resources.Parser_Context_Not_Set);
            }

            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            using (PushSpanContextConfig(DefaultMarkupSpanContext))
            {
                var builder = pooledResult.Builder;
                if (!NextToken())
                {
                    return null;
                }

                AcceptWhile(IsSpacingToken(includeNewLines: true));

                if (CurrentToken.Kind == SyntaxKind.OpenAngle)
                {
                    // "<" => Implicit Tag Block
                    ParseTagBlock(builder, new Stack<Tuple<SyntaxToken, SourceLocation>>());
                }
                else if (CurrentToken.Kind == SyntaxKind.Transition)
                {
                    // "@" => Explicit Tag/Single Line Block OR Template

                    // Output whitespace
                    builder.Add(OutputAsMarkupLiteral());

                    // Definitely have a transition span
                    Assert(SyntaxKind.Transition);
                    AcceptAndMoveNext();
                    SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                    SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                    var transition = GetNodeWithSpanContext(SyntaxFactory.MarkupTransition(Output()));
                    builder.Add(transition);
                    if (At(SyntaxKind.Transition))
                    {
                        SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                        AcceptAndMoveNext();
                        builder.Add(OutputAsMetaCode(Output(), AcceptedCharactersInternal.Any));
                    }
                    ParseAfterTransition(builder);
                }
                else
                {
                    Context.ErrorSink.OnError(
                        RazorDiagnosticFactory.CreateParsing_MarkupBlockMustStartWithTag(
                            new SourceSpan(CurrentStart, CurrentToken.Content.Length)));
                }
                builder.Add(OutputAsMarkupLiteral());

                var markupBlock = builder.ToList();

                return SyntaxFactory.MarkupBlock(markupBlock);
            }
        }

        private void ParseAfterTransition(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            // "@:" => Explicit Single Line Block
            if (CurrentToken.Kind == SyntaxKind.Text && CurrentToken.Content.Length > 0 && CurrentToken.Content[0] == ':')
            {
                // Split the token
                var split = Language.SplitToken(CurrentToken, 1, SyntaxKind.Colon);

                // The first part (left) is added to this span and we return a MetaCode span
                Accept(split.Item1);
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                builder.Add(OutputAsMetaCode(Output(), AcceptedCharactersInternal.Any));
                if (split.Item2 != null)
                {
                    Accept(split.Item2);
                }
                NextToken();
                ParseSingleLineMarkup(builder);
            }
            else if (CurrentToken.Kind == SyntaxKind.OpenAngle)
            {
                ParseTagBlock(builder, new Stack<Tuple<SyntaxToken, SourceLocation>>());
            }
        }

        private void ParseSingleLineMarkup(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            // Parse until a newline, it's that simple!
            // First, signal to code parser that whitespace is significant to us.
            var old = Context.WhiteSpaceIsSignificantToAncestorBlock;
            Context.WhiteSpaceIsSignificantToAncestorBlock = true;
            SpanContext.EditHandler = new SpanEditHandler(Language.TokenizeString);
            SkipToAndParseCode(builder, SyntaxKind.NewLine);
            if (!EndOfFile && CurrentToken.Kind == SyntaxKind.NewLine)
            {
                AcceptAndMoveNext();
                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
            }
            PutCurrentBack();
            Context.WhiteSpaceIsSignificantToAncestorBlock = old;
            builder.Add(OutputAsMarkupLiteral());
        }

        private void ParseTagBlock(in SyntaxListBuilder<RazorSyntaxNode> builder, Stack<Tuple<SyntaxToken, SourceLocation>> tags)
        {
            // TODO: This is really ugly and needs to be cleaned up.

            // Skip Whitespace and Text
            var completeTag = false;
            var blockAlreadyBuilt = false;
            do
            {
                SkipToAndParseCode(builder, SyntaxKind.OpenAngle);

                // Output everything prior to the OpenAngle into a markup span
                builder.Add(OutputAsMarkupLiteral());

                var tagBuilder = builder;
                IDisposable tagBuilderDisposable = null;
                try
                {
                    if (EndOfFile)
                    {
                        // Do not want to start a new tag block if we're at the end of the file.
                        EndTagBlock(builder, tags, complete: true);
                    }
                    else
                    {
                        var atSpecialTag = AtSpecialTag;
                        if (!atSpecialTag)
                        {
                            // Start a tag block.  This is used to wrap things like <p> or <a class="btn"> etc.
                            var pooledResult = Pool.Allocate<RazorSyntaxNode>();
                            tagBuilderDisposable = pooledResult;
                            tagBuilder = pooledResult.Builder;
                        }
                        _bufferedOpenAngle = null;
                        _lastTagStart = CurrentStart;
                        Assert(SyntaxKind.OpenAngle);
                        _bufferedOpenAngle = CurrentToken;
                        var tagStart = CurrentStart;
                        if (!NextToken())
                        {
                            Accept(_bufferedOpenAngle);
                            EndTagBlock(tagBuilder, tags, complete: false);
                        }
                        else if (atSpecialTag && At(SyntaxKind.Bang))
                        {
                            Accept(_bufferedOpenAngle);
                            completeTag = ParseBangTag(builder);
                            blockAlreadyBuilt = completeTag;
                        }
                        else
                        {
                            var result = ParseAfterTagStart(tagBuilder, builder, tagStart, tags);
                            completeTag = result.Item1;
                            blockAlreadyBuilt = result.Item2;
                        }
                    }

                    if (completeTag)
                    {
                        // Completed tags have no accepted characters inside of blocks.
                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                    }

                    if (blockAlreadyBuilt)
                    {
                        // Output the contents of the tag into its own markup span.
                        builder.Add(OutputAsMarkupLiteral());
                    }
                    else if (tagBuilderDisposable != null)
                    {
                        // A new tag block was started. Build it.
                        // Output the contents of the tag into its own markup span.
                        tagBuilder.Add(OutputAsMarkupLiteral());
                        var tagBlock = SyntaxFactory.MarkupTagBlock(tagBuilder.ToList());
                        builder.Add(tagBlock);
                    }
                }
                finally
                {
                    // Will be null if we were at end of file or special tag when initially created.
                    if (tagBuilderDisposable != null)
                    {
                        // End tag block
                        tagBuilderDisposable.Dispose();
                    }
                }
            }
            while (tags.Count > 0);

            EndTagBlock(builder, tags, completeTag);
        }

        private Tuple<bool, bool> ParseAfterTagStart(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            in SyntaxListBuilder<RazorSyntaxNode> parentBuilder,
            SourceLocation tagStart,
            Stack<Tuple<SyntaxToken, SourceLocation>> tags)
        {
            var blockAlreadyBuilt = false;
            if (!EndOfFile)
            {
                switch (CurrentToken.Kind)
                {
                    case SyntaxKind.ForwardSlash:
                        // End Tag
                        return ParseEndTag(builder, parentBuilder, tagStart, tags);
                    case SyntaxKind.QuestionMark:
                        // XML PI
                        Accept(_bufferedOpenAngle);
                        var complete = TryParseXmlPI(builder);
                        // No block is created for Xml PI. So return the same value as complete.
                        blockAlreadyBuilt = complete;
                        return Tuple.Create(complete, blockAlreadyBuilt);
                    default:
                        // Start Tag
                        return ParseStartTag(builder, parentBuilder, tags);
                }
            }
            if (tags.Count == 0)
            {
                Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_OuterTagMissingName(
                        new SourceSpan(CurrentStart, contentLength: 1 /* end of file */)));
            }

            return Tuple.Create(false, blockAlreadyBuilt);
        }

        private Tuple<bool, bool> ParseStartTag(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            in SyntaxListBuilder<RazorSyntaxNode> parentBuilder,
            Stack<Tuple<SyntaxToken, SourceLocation>> tags)
        {
            SyntaxToken bangToken = null;
            SyntaxToken potentialTagNameToken;

            if (At(SyntaxKind.Bang))
            {
                bangToken = CurrentToken;

                potentialTagNameToken = Lookahead(count: 1);
            }
            else
            {
                potentialTagNameToken = CurrentToken;
            }

            SyntaxToken tagName;

            if (potentialTagNameToken == null || potentialTagNameToken.Kind != SyntaxKind.Text)
            {
                tagName = SyntaxFactory.Token(SyntaxKind.Marker, string.Empty);
            }
            else if (bangToken != null)
            {
                tagName = SyntaxFactory.Token(SyntaxKind.Text, "!" + potentialTagNameToken.Content);
            }
            else
            {
                tagName = potentialTagNameToken;
            }

            var tag = Tuple.Create(tagName, _lastTagStart);

            if (tags.Count == 0 &&
                // Note tagName may contain a '!' escape character. This ensures <!text> doesn't match here.
                // <!text> tags are treated like any other escaped HTML start tag.
                string.Equals(tag.Item1.Content, SyntaxConstants.TextTagName, StringComparison.OrdinalIgnoreCase))
            {
                builder.Add(OutputAsMarkupLiteral());
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null;

                Accept(_bufferedOpenAngle);
                var textLocation = CurrentStart;
                Assert(SyntaxKind.Text);

                AcceptAndMoveNext();

                var bookmark = CurrentStart.AbsoluteIndex;
                var tokens = ReadWhile(IsSpacingToken(includeNewLines: true));
                var empty = At(SyntaxKind.ForwardSlash);
                if (empty)
                {
                    Accept(tokens);
                    Assert(SyntaxKind.ForwardSlash);
                    AcceptAndMoveNext();
                    bookmark = CurrentStart.AbsoluteIndex;
                    tokens = ReadWhile(IsSpacingToken(includeNewLines: true));
                }

                if (!TryAccept(SyntaxKind.CloseAngle))
                {
                    Context.Source.Position = bookmark;
                    NextToken();
                    Context.ErrorSink.OnError(
                        RazorDiagnosticFactory.CreateParsing_TextTagCannotContainAttributes(
                            new SourceSpan(textLocation, contentLength: 4 /* text */)));

                    RecoverTextTag();
                }
                else
                {
                    Accept(tokens);
                    SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                }

                if (!empty)
                {
                    tags.Push(tag);
                }

                var transition = GetNodeWithSpanContext(SyntaxFactory.MarkupTransition(Output()));
                builder.Add(transition);
                var tagBlock = SyntaxFactory.MarkupTagBlock(builder.ToList());
                parentBuilder.Add(tagBlock);

                return Tuple.Create(true, true);
            }

            Accept(_bufferedOpenAngle);
            TryParseBangEscape(builder);
            TryAccept(SyntaxKind.Text);
            
            if (At(SyntaxKind.CloseAngle))
            {
                // Completed tags have no accepted characters inside of blocks.
                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
            }
            // Output open angle and tag name
            builder.Add(OutputAsMarkupLiteral());
            return ParseRestOfTag(builder, parentBuilder, tag, tags);
        }

        private Tuple<bool, bool> ParseRestOfTag(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            in SyntaxListBuilder<RazorSyntaxNode> parentBuilder,
            Tuple<SyntaxToken, SourceLocation> tag,
            Stack<Tuple<SyntaxToken, SourceLocation>> tags)
        {
            var blockAlreadyBuilt = false;
            ParseAttributes(builder);

            // We are now at a possible end of the tag
            // Found '<', so we just abort this tag.
            if (At(SyntaxKind.OpenAngle))
            {
                return Tuple.Create(false, blockAlreadyBuilt);
            }

            var isEmpty = At(SyntaxKind.ForwardSlash);
            // Found a solidus, so don't accept it but DON'T push the tag to the stack
            if (isEmpty)
            {
                AcceptAndMoveNext();
            }

            // Check for the '>' to determine if the tag is finished
            var seenClose = TryAccept(SyntaxKind.CloseAngle);
            if (!seenClose)
            {
                Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_UnfinishedTag(
                        new SourceSpan(
                            SourceLocationTracker.Advance(tag.Item2, "<"),
                            Math.Max(tag.Item1.Content.Length, 1)),
                        tag.Item1.Content));
            }
            else
            {
                if (!isEmpty)
                {
                    // Is this a void element?
                    var tagName = tag.Item1.Content.Trim();
                    if (ParserHelpers.VoidElements.Contains(tagName))
                    {
                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                        builder.Add(OutputAsMarkupLiteral());
                        var tagBlock = SyntaxFactory.MarkupTagBlock(builder.ToList());
                        parentBuilder.Add(tagBlock);
                        blockAlreadyBuilt = true;

                        // Technically, void elements like "meta" are not allowed to have end tags. Just in case they do,
                        // we need to look ahead at the next set of tokens. If we see "<", "/", tag name, accept it and the ">" following it
                        // Place a bookmark
                        var bookmark = CurrentStart.AbsoluteIndex;

                        // Skip whitespace
                        var whiteSpace = ReadWhile(IsSpacingToken(includeNewLines: true));

                        // Open Angle
                        if (At(SyntaxKind.OpenAngle) && NextIs(SyntaxKind.ForwardSlash))
                        {
                            var openAngle = CurrentToken;
                            NextToken();
                            Assert(SyntaxKind.ForwardSlash);
                            var solidus = CurrentToken;
                            NextToken();
                            if (At(SyntaxKind.Text) && string.Equals(CurrentToken.Content, tagName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Accept up to here
                                Accept(whiteSpace);
                                parentBuilder.Add(OutputAsMarkupLiteral()); // Output the whitespace

                                using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                                {
                                    var tagBuilder = pooledResult.Builder;
                                    Accept(openAngle);
                                    Accept(solidus);
                                    AcceptAndMoveNext();

                                    // Accept to '>', '<' or EOF
                                    AcceptUntil(SyntaxKind.CloseAngle, SyntaxKind.OpenAngle);
                                    // Accept the '>' if we saw it. And if we do see it, we're complete
                                    var complete = TryAccept(SyntaxKind.CloseAngle);

                                    if (complete)
                                    {
                                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                                    }

                                    // Output the closing void element
                                    tagBuilder.Add(OutputAsMarkupLiteral());
                                    parentBuilder.Add(SyntaxFactory.MarkupTagBlock(tagBuilder.ToList()));

                                    return Tuple.Create(complete, blockAlreadyBuilt);
                                }
                            }
                        }

                        // Go back to the bookmark and just finish this tag at the close angle
                        Context.Source.Position = bookmark;
                        NextToken();
                    }
                    else if (string.Equals(tagName, ScriptTagName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!CurrentScriptTagExpectsHtml(builder))
                        {
                            SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                            builder.Add(OutputAsMarkupLiteral());
                            var tagBlock = SyntaxFactory.MarkupTagBlock(builder.ToList());
                            parentBuilder.Add(tagBlock);
                            blockAlreadyBuilt = true;

                            SkipToEndScriptAndParseCode(parentBuilder, endTagAcceptedCharacters: AcceptedCharactersInternal.None);
                        }
                        else
                        {
                            // Push the script tag onto the tag stack, it should be treated like all other HTML tags.
                            tags.Push(tag);
                        }
                    }
                    else
                    {
                        // Push the tag on to the stack
                        tags.Push(tag);
                    }
                }
            }
            return Tuple.Create(seenClose, blockAlreadyBuilt);
        }

        private Tuple<bool, bool> ParseEndTag(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            in SyntaxListBuilder<RazorSyntaxNode> parentBuilder,
            SourceLocation tagStart,
            Stack<Tuple<SyntaxToken, SourceLocation>> tags)
        {
            // Accept "/" and move next
            Assert(SyntaxKind.ForwardSlash);
            var forwardSlash = CurrentToken;
            if (!NextToken())
            {
                Accept(_bufferedOpenAngle);
                Accept(forwardSlash);
                return Tuple.Create(false, false);
            }
            else
            {
                var tagName = string.Empty;
                SyntaxToken bangToken = null;

                if (At(SyntaxKind.Bang))
                {
                    bangToken = CurrentToken;

                    var nextToken = Lookahead(count: 1);

                    if (nextToken != null && nextToken.Kind == SyntaxKind.Text)
                    {
                        tagName = "!" + nextToken.Content;
                    }
                }
                else if (At(SyntaxKind.Text))
                {
                    tagName = CurrentToken.Content;
                }

                var matched = RemoveTag(tags, tagName, tagStart);

                if (tags.Count == 0 &&
                    // Note tagName may contain a '!' escape character. This ensures </!text> doesn't match here.
                    // </!text> tags are treated like any other escaped HTML end tag.
                    string.Equals(tagName, SyntaxConstants.TextTagName, StringComparison.OrdinalIgnoreCase) &&
                    matched)
                {
                    return EndTextTag(builder, parentBuilder, forwardSlash);
                }
                Accept(_bufferedOpenAngle);
                Accept(forwardSlash);

                TryParseBangEscape(builder);

                AcceptUntil(SyntaxKind.CloseAngle);

                // Accept the ">"
                return Tuple.Create(TryAccept(SyntaxKind.CloseAngle), false);
            }
        }

        private Tuple<bool, bool> EndTextTag(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            in SyntaxListBuilder<RazorSyntaxNode> parentBuilder,
            SyntaxToken solidus)
        {
            Accept(_bufferedOpenAngle);
            Accept(solidus);

            var textLocation = CurrentStart;
            Assert(SyntaxKind.Text);
            AcceptAndMoveNext();

            var seenCloseAngle = TryAccept(SyntaxKind.CloseAngle);

            if (!seenCloseAngle)
            {
                Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_TextTagCannotContainAttributes(
                        new SourceSpan(textLocation, contentLength: 4 /* text */)));

                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.Any;
                RecoverTextTag();
            }
            else
            {
                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
            }

            SpanContext.ChunkGenerator = SpanChunkGenerator.Null;

            var transition = GetNodeWithSpanContext(SyntaxFactory.MarkupTransition(Output()));
            builder.Add(transition);
            var tagBlock = SyntaxFactory.MarkupTagBlock(builder.ToList());
            parentBuilder.Add(tagBlock);

            return Tuple.Create(seenCloseAngle, true);
        }

        private bool RemoveTag(Stack<Tuple<SyntaxToken, SourceLocation>> tags, string tagName, SourceLocation tagStart)
        {
            Tuple<SyntaxToken, SourceLocation> currentTag = null;
            while (tags.Count > 0)
            {
                currentTag = tags.Pop();
                if (string.Equals(tagName, currentTag.Item1.Content, StringComparison.OrdinalIgnoreCase))
                {
                    // Matched the tag
                    return true;
                }
            }
            if (currentTag != null)
            {
                Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_MissingEndTag(
                        new SourceSpan(
                            SourceLocationTracker.Advance(currentTag.Item2, "<"),
                            currentTag.Item1.Content.Length),
                        currentTag.Item1.Content));
            }
            else
            {
                Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_UnexpectedEndTag(
                        new SourceSpan(SourceLocationTracker.Advance(tagStart, "</"), tagName.Length), tagName));
            }
            return false;
        }

        private void RecoverTextTag()
        {
            // We don't want to skip-to and parse because there shouldn't be anything in the body of text tags.
            AcceptUntil(SyntaxKind.CloseAngle, SyntaxKind.NewLine);

            // Include the close angle in the text tag block if it's there, otherwise just move on
            TryAccept(SyntaxKind.CloseAngle);
        }

        private void EndTagBlock(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            Stack<Tuple<SyntaxToken, SourceLocation>> tags,
            bool complete)
        {
            if (tags.Count > 0)
            {
                // Ended because of EOF, not matching close tag.  Throw error for last tag
                while (tags.Count > 1)
                {
                    tags.Pop();
                }
                var tag = tags.Pop();
                Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_MissingEndTag(
                        new SourceSpan(
                            SourceLocationTracker.Advance(tag.Item2, "<"),
                            tag.Item1.Content.Length),
                        tag.Item1.Content));
            }
            else if (complete)
            {
                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
            }
            tags.Clear();
            if (!Context.DesignTimeMode)
            {
                var shouldAcceptWhitespaceAndNewLine = true;

                // Check if the previous span was a transition.
                var previousSpan = builder.Count > 0 ? GetLastSpan(builder[builder.Count - 1]) : null;
                if (previousSpan != null && previousSpan.Kind == SyntaxKind.MarkupTransition)
                {
                    var tokens = ReadWhile(
                        f => (f.Kind == SyntaxKind.Whitespace) || (f.Kind == SyntaxKind.NewLine));

                    // Make sure the current token is not markup, which can be html start tag or @:
                    if (!(At(SyntaxKind.OpenAngle) ||
                        (At(SyntaxKind.Transition) && Lookahead(count: 1).Content.StartsWith(":"))))
                    {
                        // Don't accept whitespace as markup if the end text tag is followed by csharp.
                        shouldAcceptWhitespaceAndNewLine = false;
                    }

                    PutCurrentBack();
                    PutBack(tokens);
                    EnsureCurrent();
                }

                if (shouldAcceptWhitespaceAndNewLine)
                {
                    // Accept whitespace and a single newline if present
                    AcceptWhile(SyntaxKind.Whitespace);
                    TryAccept(SyntaxKind.NewLine);
                }
            }
            else if (SpanContext.EditHandler.AcceptedCharacters == AcceptedCharactersInternal.Any)
            {
                AcceptWhile(SyntaxKind.Whitespace);
                TryAccept(SyntaxKind.NewLine);
            }
            PutCurrentBack();

            if (!complete)
            {
                AcceptMarkerTokenIfNecessary();
            }

            builder.Add(OutputAsMarkupLiteral());
        }

        public MarkupBlockSyntax ParseRazorBlock(Tuple<string, string> nestingSequences, bool caseSensitive)
        {
            if (Context == null)
            {
                throw new InvalidOperationException(Resources.Parser_Context_Not_Set);
            }

            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            using (PushSpanContextConfig(DefaultMarkupSpanContext))
            {
                var builder = pooledResult.Builder;

                NextToken();
                CaseSensitive = caseSensitive;
                NestingBlock(builder, nestingSequences);
                AcceptMarkerTokenIfNecessary();
                builder.Add(OutputAsMarkupLiteral());

                return SyntaxFactory.MarkupBlock(builder.ToList());
            }
        }

        private void NestingBlock(in SyntaxListBuilder<RazorSyntaxNode> builder, Tuple<string, string> nestingSequences)
        {
            var nesting = 1;
            while (nesting > 0 && !EndOfFile)
            {
                SkipToAndParseCode(builder, token =>
                    token.Kind == SyntaxKind.Text ||
                    token.Kind == SyntaxKind.OpenAngle);
                if (At(SyntaxKind.Text))
                {
                    nesting += ProcessTextToken(builder, nestingSequences, nesting);
                    if (CurrentToken != null)
                    {
                        AcceptAndMoveNext();
                    }
                    else if (nesting > 0)
                    {
                        NextToken();
                    }
                }
                else
                {
                    ParseTagInDocumentContext(builder);
                }
            }
        }

        private int ProcessTextToken(in SyntaxListBuilder<RazorSyntaxNode> builder, Tuple<string, string> nestingSequences, int currentNesting)
        {
            for (var i = 0; i < CurrentToken.Content.Length; i++)
            {
                var nestingDelta = HandleNestingSequence(builder, nestingSequences.Item1, i, currentNesting, 1);
                if (nestingDelta == 0)
                {
                    nestingDelta = HandleNestingSequence(builder, nestingSequences.Item2, i, currentNesting, -1);
                }

                if (nestingDelta != 0)
                {
                    return nestingDelta;
                }
            }
            return 0;
        }

        private int HandleNestingSequence(in SyntaxListBuilder<RazorSyntaxNode> builder, string sequence, int position, int currentNesting, int retIfMatched)
        {
            if (sequence != null &&
                CurrentToken.Content[position] == sequence[0] &&
                position + sequence.Length <= CurrentToken.Content.Length)
            {
                var possibleStart = CurrentToken.Content.Substring(position, sequence.Length);
                if (string.Equals(possibleStart, sequence, Comparison))
                {
                    // Capture the current token and "put it back" (really we just want to clear CurrentToken)
                    var bookmark = CurrentStart;
                    var token = CurrentToken;
                    PutCurrentBack();

                    // Carve up the token
                    var pair = Language.SplitToken(token, position, SyntaxKind.Text);
                    var preSequence = pair.Item1;
                    Debug.Assert(pair.Item2 != null);
                    pair = Language.SplitToken(pair.Item2, sequence.Length, SyntaxKind.Text);
                    var sequenceToken = pair.Item1;
                    var postSequence = pair.Item2;
                    var postSequenceBookmark = bookmark.AbsoluteIndex + preSequence.Content.Length + pair.Item1.Content.Length;

                    // Accept the first chunk (up to the nesting sequence we just saw)
                    if (!string.IsNullOrEmpty(preSequence.Content))
                    {
                        Accept(preSequence);
                    }

                    if (currentNesting + retIfMatched == 0)
                    {
                        // This is 'popping' the final entry on the stack of nesting sequences
                        // A caller higher in the parsing stack will accept the sequence token, so advance
                        // to it
                        Context.Source.Position = bookmark.AbsoluteIndex + preSequence.Content.Length;
                    }
                    else
                    {
                        // This isn't the end of the last nesting sequence, accept the token and keep going
                        Accept(sequenceToken);

                        // Position at the start of the postSequence token, which might be null.
                        Context.Source.Position = postSequenceBookmark;
                    }

                    // Return the value we were asked to return if matched, since we found a nesting sequence
                    return retIfMatched;
                }
            }
            return 0;
        }

        private Syntax.GreenNode GetLastSpan(RazorSyntaxNode node)
        {
            if (node == null)
            {
                return null;
            }

            // Find the last token of this node and return its immediate non-list parent.
            var red = node.CreateRed();
            var last = red.GetLastTerminal();
            if (last == null)
            {
                return null;
            }

            while (last.Green.IsToken || last.Green.IsList)
            {
                last = last.Parent;
            }

            return last.Green;
        }
    }
}
