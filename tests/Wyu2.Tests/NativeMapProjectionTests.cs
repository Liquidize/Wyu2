using System.Numerics;
using Wyu2.Game;

namespace Wyu2.Tests;

public class NativeMapProjectionTests
{
    // A Thanalan sized sheet drawn into a 512 pixel square starting a little way down the screen.
    private const ushort SizeFactor = 200;
    private static readonly Vector2 SheetMin = new(100f, 60f);
    private static readonly Vector2 SheetSize = new(512f, 512f);

    [Fact]
    public void TheSheetProjectionAgreesWithTheTextureCoordinatesTheMapWindowUses()
    {
        var projection = NativeMapProjection.ForSheet(SheetMin, SheetSize, SizeFactor, -32, 96);

        foreach (var world in new[] { new Vector3(0f, 0f, 0f), new Vector3(214f, 12f, -488f), new Vector3(-90f, 0f, 33f) })
        {
            var uv = MapGeometry.WorldToTextureUv(world.X, world.Z, SizeFactor, -32, 96);
            var expected = SheetMin + (uv * SheetSize);
            var actual = projection.Project(world);

            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
        }
    }

    [Fact]
    public void TheMiddleOfTheSheetIsWhateverWorldPointTheOffsetsCancel()
    {
        var projection = NativeMapProjection.ForSheet(SheetMin, SheetSize, SizeFactor, -32, 96);
        var centre = projection.Project(new Vector3(32f, 0f, -96f));

        Assert.Equal(SheetMin.X + 256f, centre.X, 3);
        Assert.Equal(SheetMin.Y + 256f, centre.Y, 3);
    }

    [Fact]
    public void PanningTheSheetMovesEveryMarkerWithIt()
    {
        var world = new Vector3(148f, 0f, -260f);
        var still = NativeMapProjection.ForSheet(SheetMin, SheetSize, SizeFactor, 0, 0).Project(world);
        var panned = NativeMapProjection.ForSheet(SheetMin + new Vector2(70f, -25f), SheetSize, SizeFactor, 0, 0)
            .Project(world);

        Assert.Equal(70f, panned.X - still.X, 3);
        Assert.Equal(-25f, panned.Y - still.Y, 3);
    }

    [Fact]
    public void ZoomingTheSheetMovesMarkersAwayFromTheMiddleInProportion()
    {
        var world = new Vector3(148f, 0f, -260f);
        var normal = NativeMapProjection.ForSheet(SheetMin, SheetSize, SizeFactor, 0, 0);
        var zoomed = NativeMapProjection.ForSheet(SheetMin, SheetSize * 2f, SizeFactor, 0, 0);

        var fromCentre = normal.Project(world) - (SheetMin + (SheetSize / 2f));
        var zoomedFromCentre = zoomed.Project(world) - (SheetMin + SheetSize);

        Assert.Equal(fromCentre.X * 2f, zoomedFromCentre.X, 3);
        Assert.Equal(fromCentre.Y * 2f, zoomedFromCentre.Y, 3);
    }

    [Fact]
    public void ABiggerSizeFactorSpreadsTheSameZoneFurtherApart()
    {
        var world = new Vector3(100f, 0f, 0f);
        var small = NativeMapProjection.ForSheet(SheetMin, SheetSize, 100, 0, 0).Project(world);
        var large = NativeMapProjection.ForSheet(SheetMin, SheetSize, 400, 0, 0).Project(world);
        var centre = SheetMin + (SheetSize / 2f);

        Assert.Equal((small.X - centre.X) * 4f, large.X - centre.X, 3);
    }

    [Fact]
    public void ThePlayerSitsExactlyOnTheMinimapCentreWhicheverWayTheCameraFaces()
    {
        var player = new Vector3(-412.5f, 18f, 77.25f);
        var centre = new Vector2(1700f, 140f);

        foreach (var rotation in new[] { 0f, 1.2f, -2.7f, MathF.PI })
        {
            var projection = NativeMapProjection.ForPlayer(centre, player, 1.4f, rotation);
            var point = projection.Project(player);

            Assert.Equal(centre.X, point.X, 4);
            Assert.Equal(centre.Y, point.Y, 4);
        }
    }

    [Fact]
    public void ANorthUpMapPutsEastToTheRightAndSouthBelow()
    {
        var player = new Vector3(0f, 0f, 0f);
        var projection = NativeMapProjection.ForPlayer(new Vector2(0f, 0f), player, 2f, 0f);

        Assert.Equal(new Vector2(20f, 0f), projection.Project(new Vector3(10f, 0f, 0f)));
        Assert.Equal(new Vector2(0f, 20f), projection.Project(new Vector3(0f, 0f, 10f)));
    }

    [Fact]
    public void FacingNorthLeavesTheMapAlone()
    {
        Assert.Equal(0f, NativeMapProjection.RotationForCamera(0f, -1f), 5);
    }

    [Theory]
    // Camera forward, then where somebody ten yalms due north of the player should be drawn.
    [InlineData(0f, -1f, 0f, -10f)]      // Looking north: north is up.
    [InlineData(0f, 1f, 0f, 10f)]        // Looking south: north is behind, so it falls below.
    [InlineData(1f, 0f, -10f, 0f)]       // Looking east: north is off to the left.
    [InlineData(-1f, 0f, 10f, 0f)]       // Looking west: north is off to the right.
    public void TheDirectionTheCameraIsLookingEndsUpAtTheTop(
        float forwardX, float forwardZ, float expectedX, float expectedY)
    {
        var rotation = NativeMapProjection.RotationForCamera(forwardX, forwardZ);
        var projection = NativeMapProjection.ForPlayer(Vector2.Zero, Vector3.Zero, 1f, rotation);

        var north = projection.Project(new Vector3(0f, 0f, -10f));

        Assert.Equal(expectedX, north.X, 4);
        Assert.Equal(expectedY, north.Y, 4);
    }

    [Fact]
    public void ACameraPointingStraightDownLeavesTheMapUnturnedRatherThanSpinning()
    {
        Assert.Equal(0f, NativeMapProjection.RotationForCamera(0f, 0f), 5);
    }

    [Fact]
    public void MarkerScalingCarriesBothTheZoomAndTheZoneSize()
    {
        // One map unit is one pixel of the sheet; a size factor of 200 packs two of them into a yalm.
        Assert.Equal(2f, NativeMapProjection.PixelsPerYalmFromMarkerScaling(200, 1f, 1f), 5);
        Assert.Equal(3f, NativeMapProjection.PixelsPerYalmFromMarkerScaling(100, 1.5f, 2f), 5);
    }

    [Theory]
    [InlineData((ushort)200, 0f, 1f)]
    [InlineData((ushort)200, 1f, 0f)]
    [InlineData((ushort)200, -1f, 1f)]
    public void AMapThatReportsNoScaleIsRefusedRatherThanGuessedAt(
        ushort sizeFactor, float markerScaling, float nodeScale)
    {
        var pixels = NativeMapProjection.PixelsPerYalmFromMarkerScaling(sizeFactor, markerScaling, nodeScale);

        Assert.Equal(0f, pixels);
        Assert.False(new NativeMapProjection(Vector2.Zero, Vector2.Zero, pixels, 0f).IsUsable);
    }

    [Fact]
    public void AMissingSizeFactorFallsBackToOneToOneRatherThanCollapsing()
    {
        Assert.Equal(1f, NativeMapProjection.PixelsPerYalmFromMarkerScaling(0, 1f, 1f), 5);
    }

    [Fact]
    public void PointsInsideTheMinimapAreLeftWhereTheyAre()
    {
        var point = new Vector2(105f, 98f);
        Assert.True(NativeMapProjection.TryFitInCircle(new Vector2(100f, 100f), 50f, point, false, out var fitted));
        Assert.Equal(point, fitted);
    }

    [Fact]
    public void PointsPastTheRimAreEitherPinnedToItOrDropped()
    {
        var centre = new Vector2(100f, 100f);
        var far = new Vector2(400f, 100f);

        Assert.False(NativeMapProjection.TryFitInCircle(centre, 50f, far, false, out _));

        Assert.True(NativeMapProjection.TryFitInCircle(centre, 50f, far, true, out var pinned));
        Assert.Equal(150f, pinned.X, 4);
        Assert.Equal(100f, pinned.Y, 4);
        Assert.Equal(50f, (pinned - centre).Length(), 4);
    }

    [Fact]
    public void AMinimapWithNoRadiusDrawsNothingAtAll()
    {
        Assert.False(NativeMapProjection.TryFitInCircle(Vector2.Zero, 0f, Vector2.Zero, true, out _));
    }

    [Fact]
    public void APointExactlyOnTheCentreSurvivesTheClamp()
    {
        var centre = new Vector2(100f, 100f);
        Assert.True(NativeMapProjection.TryFitInCircle(centre, 50f, centre, true, out var fitted));
        Assert.Equal(centre, fitted);
    }

    [Fact]
    public void RotatingTwiceByHalfATurnComesBackToWhereItStarted()
    {
        var offset = new Vector2(13f, -7f);
        var turned = NativeMapProjection.Rotate(NativeMapProjection.Rotate(offset, MathF.PI), MathF.PI);

        Assert.Equal(offset.X, turned.X, 4);
        Assert.Equal(offset.Y, turned.Y, 4);
    }

    [Fact]
    public void RotationPreservesDistance()
    {
        var offset = new Vector2(30f, 40f);
        Assert.Equal(50f, NativeMapProjection.Rotate(offset, 0.9f).Length(), 4);
    }
}
