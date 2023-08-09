﻿using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Html.Dom;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using OneOf;
using ShapeCrawler.Drawing;
using ShapeCrawler.Extensions;
using ShapeCrawler.Placeholders;
using ShapeCrawler.Services;
using ShapeCrawler.Shapes;
using ShapeCrawler.Shared;
using ShapeCrawler.Texts;
using SkiaSharp;
using P = DocumentFormat.OpenXml.Presentation;

namespace ShapeCrawler.AutoShapes;

internal class SCAutoShape : SCShape, IAutoShape, ITextFrameContainer
{
    // SkiaSharp uses 72 Dpi (https://stackoverflow.com/a/69916569/2948684), ShapeCrawler uses 96 Dpi.
    // 96/72=1.4
    private const double Scale = 1.4;

    private readonly Lazy<SCShapeFill> shapeFill;
    private readonly Lazy<SCTextFrame?> textFrame;
    private readonly ResetableLazy<Dictionary<int, FontData>> lvlToFontData;
    private readonly TypedOpenXmlCompositeElement pShape;
    private readonly ISlideStructure slideStructure;
    private readonly TypedOpenXmlPart slideTypedOpenXmlPart;

    internal SCAutoShape(
        TypedOpenXmlCompositeElement pShape,
        OneOf<SCSlide, SCSlideLayout, SCSlideMaster> slideOf,
        OneOf<SCSlideShapes, SCSlideGroupShape> shapeCollectionOf, 
        TypedOpenXmlPart slideTypedOpenXmlPart)
        : base(pShape, slideOf, shapeCollectionOf)
    {
        this.pShape = pShape;
        this.slideTypedOpenXmlPart = slideTypedOpenXmlPart;
        this.textFrame = new Lazy<SCTextFrame?>(this.ParseTextFrame);
        this.shapeFill = new Lazy<SCShapeFill>(this.GetFill);
        this.lvlToFontData = new ResetableLazy<Dictionary<int, FontData>>(this.GetLvlToFontData);
        this.slideStructure = (ISlideStructure)this.slideOf.Value;
    }
    
    internal event EventHandler<NewAutoShape>? Duplicated;

    #region Public Properties

    public IShapeOutline Outline => this.GetOutline();

    public SCShape SCShape => this; // TODO: should be internal?

    public override SCShapeType ShapeType => SCShapeType.AutoShape;

    public virtual IShapeFill? Fill => this.shapeFill.Value;
    
    public virtual ITextFrame? TextFrame => this.textFrame.Value;

    public virtual IAutoShape Duplicate()
    {
        var typedCompositeElement = (TypedOpenXmlCompositeElement)this.PShapeTreeChild.CloneNode(true);
        var id = this.GetNextShapeId();
        typedCompositeElement.GetNonVisualDrawingProperties().Id = new UInt32Value((uint)id);
        var newAutoShape = new SCAutoShape(
            typedCompositeElement, 
            this.slideOf,
            this.shapeCollectionOf,
            this.slideTypedOpenXmlPart);

        var duplicatedShape = new NewAutoShape(newAutoShape, typedCompositeElement);
        this.Duplicated?.Invoke(this, duplicatedShape);
        
        return newAutoShape;
    }

    private int GetNextShapeId()
    {
        var slide = (ISlideStructure)this.slideOf.Value;
        if (slide.Shapes.Any())
        {
            return slide.Shapes.Select(shape => shape.Id).Prepend(0).Max() + 1;    
        }

        return 1;
    }
    
    #endregion Public Properties

    internal override void Draw(SKCanvas slideCanvas)
    {
        var skColorOutline = SKColor.Parse(this.Outline.Color);

        using var paint = new SKPaint
        {
            Color = skColorOutline,
            IsAntialias = true,
            StrokeWidth = UnitConverter.PointToPixel(this.Outline.Weight),
            Style = SKPaintStyle.Stroke
        };

        if (this.GeometryType == SCGeometry.Rectangle)
        {
            float left = this.X;
            float top = this.Y;
            float right = this.X + this.Width;
            float bottom = this.Y + this.Height;
            var rect = new SKRect(left, top, right, bottom);
            slideCanvas.DrawRect(rect, paint);
            var textFrame = (SCTextFrame)this.TextFrame!;
            textFrame.Draw(slideCanvas, left, this.Y);
        }
    }

    internal override string ToJson()
    {
        throw new NotImplementedException();
    }

    internal override IHtmlElement ToHtmlElement()
    {
        throw new NotImplementedException();
    }

    internal void ResizeShape()
    {
        if (this.TextFrame!.AutofitType != SCAutofitType.Resize)
        {
            return;
        }

        var baseParagraph = this.TextFrame.Paragraphs.First();
        var popularPortion = baseParagraph.Portions.OfType<SCRegularPortion>().GroupBy(p => p.Font.Size).OrderByDescending(x => x.Count())
            .First().First();
        var font = popularPortion.Font;

        var paint = new SKPaint();
        var fontSize = font!.Size;
        paint.TextSize = fontSize;
        paint.Typeface = SKTypeface.FromFamilyName(font.LatinName);
        paint.IsAntialias = true;

        var lMarginPixel = UnitConverter.CentimeterToPixel(this.TextFrame.LeftMargin);
        var rMarginPixel = UnitConverter.CentimeterToPixel(this.TextFrame.RightMargin);
        var tMarginPixel = UnitConverter.CentimeterToPixel(this.TextFrame.TopMargin);
        var bMarginPixel = UnitConverter.CentimeterToPixel(this.TextFrame.BottomMargin);

        var textRect = default(SKRect);
        var text = this.TextFrame.Text;
        paint.MeasureText(text, ref textRect);
        var textWidth = textRect.Width;
        var textHeight = paint.TextSize;
        var currentBlockWidth = this.Width - lMarginPixel - rMarginPixel;
        var currentBlockHeight = this.Height - tMarginPixel - bMarginPixel;

        this.UpdateHeight(textWidth, currentBlockWidth, textHeight, tMarginPixel, bMarginPixel, currentBlockHeight);
        this.UpdateWidthIfNeed(paint, lMarginPixel, rMarginPixel);
    }

    internal void FillFontData(int paragraphLvl, ref FontData fontData)
    {
        if (this.lvlToFontData.Value.TryGetValue(paragraphLvl, out var layoutFontData))
        {
            fontData = layoutFontData;
            if (!fontData.IsFilled() && this.Placeholder != null)
            {
                var placeholder = (SCPlaceholder)this.Placeholder;
                var referencedMasterShape = (SCAutoShape?)placeholder.ReferencedShape.Value;
                referencedMasterShape?.FillFontData(paragraphLvl, ref fontData);
            }

            return;
        }

        if (this.Placeholder != null)
        {
            var placeholder = (SCPlaceholder)this.Placeholder;
            var referencedMasterShape = (SCAutoShape?)placeholder.ReferencedShape.Value;
            if (referencedMasterShape != null)
            {
                referencedMasterShape.FillFontData(paragraphLvl, ref fontData);
            }
        }
    }

    private Dictionary<int, FontData> GetLvlToFontData()
    {
        var textBody = this.pShape.GetFirstChild<DocumentFormat.OpenXml.Presentation.TextBody>();
        var lvlToFontData = FontDataParser.FromCompositeElement(textBody!.ListStyle!);

        if (!lvlToFontData.Any())
        {
            var endParaRunPrFs = textBody.GetFirstChild<DocumentFormat.OpenXml.Drawing.Paragraph>() !
                .GetFirstChild<DocumentFormat.OpenXml.Drawing.EndParagraphRunProperties>()?.FontSize;
            if (endParaRunPrFs is not null)
            {
                var fontData = new FontData
                {
                    FontSize = endParaRunPrFs
                };
                lvlToFontData.Add(1, fontData);
            }
        }

        return lvlToFontData;
    }

    private void UpdateHeight(
        float textWidth,
        int currentBlockWidth,
        float textHeight,
        int tMarginPixel,
        int bMarginPixel,
        int currentBlockHeight)
    {
        var requiredRowsCount = textWidth / currentBlockWidth;
        var integerPart = (int)requiredRowsCount;
        var fractionalPart = requiredRowsCount - integerPart;
        if (fractionalPart > 0)
        {
            integerPart++;
        }

        var requiredHeight = (integerPart * textHeight) + tMarginPixel + bMarginPixel;
        this.Height = (int)requiredHeight + tMarginPixel + bMarginPixel + tMarginPixel + bMarginPixel;

        // We should raise the shape up by the amount which is half of the increased offset.
        // PowerPoint does the same thing.
        var yOffset = (requiredHeight - currentBlockHeight) / 2;
        this.Y -= (int)yOffset;
    }

    private void UpdateWidthIfNeed(SKPaint paint, int lMarginPixel, int rMarginPixel)
    {
        if (!this.TextFrame!.TextWrapped)
        {
            var longerText = this.TextFrame.Paragraphs
                .Select(x => new { x.Text, x.Text.Length })
                .OrderByDescending(x => x.Length)
                .First().Text;
            var paraTextRect = default(SKRect);
            var widthInPixels = paint.MeasureText(longerText, ref paraTextRect);
            this.Width = (int)(widthInPixels * Scale) + lMarginPixel + rMarginPixel;
        }
    }

    private SCTextFrame? ParseTextFrame()
    {
        var pTextBody = this.PShapeTreeChild.GetFirstChild<DocumentFormat.OpenXml.Presentation.TextBody>();
        if (pTextBody == null)
        {
            return null;
        }

        var newTextFrame = new SCTextFrame(this, pTextBody, this.slideStructure, this);
        newTextFrame.TextChanged += this.ResizeShape;

        return newTextFrame;
    }

    private SCShapeFill GetFill()
    {
        var slideObject = this.SlideStructure;
        return new SCAutoShapeFill(
            slideObject, 
            this.pShape.GetFirstChild<P.ShapeProperties>() !, 
            this, 
            this.slideTypedOpenXmlPart);
    }

    private IShapeOutline GetOutline()
    {
        return new SCShapeOutline(this);
    }
}