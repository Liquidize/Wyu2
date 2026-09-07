using System.Numerics;
using FriendRadar.Game;

namespace FriendRadar.Tests;

public class MapGeometryTests
{
    // La Noscea style map: size factor 200, no offset.
    private const ushort SizeFactor = 200;

    [Fact]
    public void TheOriginSitsInTheMiddleOfTheSheet()
    {
        var uv = MapGeometry.WorldToTextureUv(0f, 0f, SizeFactor, 0, 0);

        Assert.Equal(0.5f, uv.X, 5);
        Assert.Equal(0.5f, uv.Y, 5);
    }

    [Theory]
    [InlineData(100f, -250f, (ushort)200, (short)0, (short)0)]
    [InlineData(-64.5f, 12.25f, (ushort)100, (short)-32, (short)16)]
    [InlineData(0f, 0f, (ushort)400, (short)128, (short)-64)]
    public void TextureCoordinatesRoundTrip(float x, float z, ushort sizeFactor, short offsetX, short offsetY)
    {
        var uv = MapGeometry.WorldToTextureUv(x, z, sizeFactor, offsetX, offsetY);
        var world = MapGeometry.TextureUvToWorld(uv, sizeFactor, offsetX, offsetY);

        Assert.Equal(x, world.X, 3);
        Assert.Equal(z, world.Y, 3);
    }

    [Fact]
    public void FullSizeMapsCentreOnTwentyOnePointFive()
    {
        // A size factor of 100 means the sheet covers the full 1..42 coordinate range players see.
        var coordinates = MapGeometry.WorldToMapCoordinates(0f, 0f, 100, 0, 0);

        Assert.Equal(21.5f, coordinates.X, 2);
        Assert.Equal(21.5f, coordinates.Y, 2);
    }

    [Fact]
    public void ZoomedMapsUseAProportionallySmallerRange()
    {
        // Double the size factor, half the coordinate span: the centre lands at 11.25.
        var coordinates = MapGeometry.WorldToMapCoordinates(0f, 0f, SizeFactor, 0, 0);

        Assert.Equal(11.25f, coordinates.X, 2);
        Assert.Equal(11.25f, coordinates.Y, 2);
    }

    [Fact]
    public void OffsetsShiftTheCoordinates()
    {
        var without = MapGeometry.WorldToMapCoordinates(0f, 0f, 100, 0, 0);
        var with = MapGeometry.WorldToMapCoordinates(0f, 0f, 100, 100, 0);

        Assert.True(with.X > without.X);
        Assert.Equal(without.Y, with.Y, 4);
    }

    [Fact]
    public void MapCoordinatesGrowEastAndSouth()
    {
        var origin = MapGeometry.WorldToMapCoordinates(0f, 0f, SizeFactor, 0, 0);
        var east = MapGeometry.WorldToMapCoordinates(100f, 0f, SizeFactor, 0, 0);
        var south = MapGeometry.WorldToMapCoordinates(0f, 100f, SizeFactor, 0, 0);

        Assert.True(east.X > origin.X);
        Assert.Equal(origin.Y, east.Y, 4);
        Assert.True(south.Y > origin.Y);
        Assert.Equal(origin.X, south.X, 4);
    }

    [Fact]
    public void ASizeFactorOfZeroDoesNotDivideByZero()
    {
        var uv = MapGeometry.WorldToTextureUv(10f, 10f, 0, 0, 0);

        Assert.False(float.IsNaN(uv.X));
        Assert.False(float.IsInfinity(uv.Y));
    }

    [Theory]
    [InlineData("s1d1/00", "ui/map/s1d1/00/s1d100_m.tex")]
    [InlineData("f1t2/00", "ui/map/f1t2/00/f1t200_m.tex")]
    public void MapTexturePathsFollowTheGameLayout(string mapId, string expected)
    {
        Assert.Equal(expected, MapGeometry.GetMapTexturePath(mapId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0000")]
    public void MapsWithoutASheetReturnNothing(string? mapId)
    {
        Assert.Null(MapGeometry.GetMapTexturePath(mapId));
    }

    [Fact]
    public void DistanceIgnoresHeight()
    {
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(3f, 500f, 4f);

        Assert.Equal(5f, MapGeometry.FlatDistance(a, b), 4);
    }

    [Fact]
    public void CoordinatesFormatToOneDecimal()
    {
        Assert.Equal("(12.3, 8.4)", MapGeometry.FormatCoordinates(new Vector2(12.34f, 8.39f)));
    }
}
