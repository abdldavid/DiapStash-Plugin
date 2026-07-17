using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace DiapStash_Plugin
{
    public class OverlayPreset
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Untitled Design";
        public string Trigger { get; set; } = ""; // E.g., a JakeyTTS command variable value to trigger this specific design

        public double CardW { get; set; } = 800; 
        public double CardH { get; set; } = 200;
        public int TransitionType { get; set; } = 0; 
        public double TransitionDurationMs { get; set; } = 400;
        public double StayOnScreenDurationMs { get; set; } = 5000;
        public bool UseRealData { get; set; } = true;
        public string CardBackgroundHex { get; set; } = "#FFFFFF"; // Default white
        public double CornerRadius { get; set; } = 12; // Default corner radius
        
        public List<OverlayElement> Elements { get; set; } = new List<OverlayElement>();
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(TextElement), typeDiscriminator: "text")]
    [JsonDerivedType(typeof(BarElement), typeDiscriminator: "bar")]
    [JsonDerivedType(typeof(ImageElement), typeDiscriminator: "image")]
    [JsonDerivedType(typeof(GroupElement), typeDiscriminator: "group")]
    [JsonDerivedType(typeof(RingElement), typeDiscriminator: "ring")]
    public abstract class OverlayElement
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string ElementType { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int ZIndex { get; set; }
        public double CornerRadius { get; set; } = 6; // Default for bars, etc.
    }

    public class GroupElement : OverlayElement
    {
        public GroupElement() { ElementType = "group"; }
        public List<OverlayElement> Children { get; set; } = new List<OverlayElement>();
    }

    public class TextElement : OverlayElement
    {
        public TextElement() { ElementType = "text"; }
        public string CustomText { get; set; } = "New Text";
        public string DataSource { get; set; } = "Custom"; // Custom, ProductName, Size, Wetness, Messiness, LiveStatus
        public string FontFamily { get; set; } = "Outfit";
        public double FontSize { get; set; } = 20;
        public string FontWeight { get; set; } = "Bold";
        public string FontStyle { get; set; } = "Normal"; // Normal, Italic
        public bool TextWrap { get; set; } = false;
        public string ColorHex { get; set; } = "#1E1E1E";
        public string TextAlignment { get; set; } = "Left"; // Left, Center, Right
        public bool ShowSeconds { get; set; } = true;
    }

    public class BarElement : OverlayElement
    {
        public BarElement() { ElementType = "bar"; }
        public string Orientation { get; set; } = "Horizontal"; // Horizontal, Vertical
        public string DataSource { get; set; } = "Wetness"; // Wetness, Messiness
        public string FillColorHex { get; set; } = "#0078D7";
        public string BgColorHex { get; set; } = "#E6E6E6";
    }

    public class ImageElement : OverlayElement
    {
        public ImageElement() { ElementType = "image"; }
        public string DataSource { get; set; } = "DiapStashImage"; // Custom, DiapStashImage
        public string CustomUrl { get; set; } = "";
        public string Stretch { get; set; } = "UniformToFill"; // Uniform, UniformToFill, Fill
    }

    public class RingElement : OverlayElement
    {
        public RingElement() { ElementType = "ring"; }
        public string DataSource { get; set; } = "Wetness"; // Wetness, Messiness
        public double StrokeThickness { get; set; } = 10;
        public bool IsFullCircle { get; set; } = true;
        public double ArcAngle { get; set; } = 360; // 0 to 360
        public double ArcRotation { get; set; } = 0; // standard rotation offset
        public bool RoundedCorners { get; set; } = true;
        public string FillColorHex { get; set; } = "#0078D7";
        public string BgColorHex { get; set; } = "#E6E6E6";
    }
}
