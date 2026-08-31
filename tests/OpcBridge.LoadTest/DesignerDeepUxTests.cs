using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.ViewModels;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Widgets;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Deep-UX designer logic: snapping, undo/redo, clipboard, alignment, nudge.
/// </summary>
public sealed class DesignerDeepUxTests
{
    private static DisplaySurfaceViewModel NewSurface(double w = 200, double h = 100)
    {
        var surface = new DisplaySurfaceViewModel(new MultiBridgeTagCache(), _ => { }, (_, _) => Task.FromResult((true, (string?)null)));
        surface.ApplyDesignMode(true);
        surface.Load(new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = "p",
            Name = "P",
            Width = (int)w,
            Height = (int)h
        });
        return surface;
    }

    private static WidgetViewModelBase AddWidget(DisplaySurfaceViewModel surface, string id = "w1", string type = "numeric", double x = 10, double y = 10, double w = 40, double h = 30)
    {
        var dto = new DisplayWidgetDto
        {
            Id = id,
            Type = type,
            X = x,
            Y = y,
            W = w,
            H = h,
            Props = new Dictionary<string, JsonElement>()
        };
        var doc = new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = "p",
            Name = "P",
            Width = (int)surface.CanvasWidth,
            Height = (int)surface.CanvasHeight,
            Widgets = [dto]
        };
        surface.Load(doc);
        return surface.Widgets[0];
    }

    [Fact]
    public void MoveWidgetTo_Snaps_WhenSnapStepSet()
    {
        var surface = NewSurface();
        surface.SnapStep = 8;
        var widget = AddWidget(surface);
        surface.MoveWidgetTo(widget, 13, 27);
        Assert.Equal(16, widget.X);
        Assert.Equal(24, widget.Y);
    }

    [Fact]
    public void MoveWidgetTo_NoSnap_WhenSnapStepNull()
    {
        var surface = NewSurface();
        surface.SnapStep = null;
        var widget = AddWidget(surface);
        surface.MoveWidgetTo(widget, 13, 27);
        Assert.Equal(13, widget.X);
        Assert.Equal(27, widget.Y);
    }

    [Fact]
    public void MoveWidgetTo_Clamps_ToCanvas()
    {
        var surface = NewSurface(200, 100);
        var widget = AddWidget(surface, w: 40, h: 30);
        surface.MoveWidgetTo(widget, 500, 500);
        Assert.Equal(160.0, widget.X);
        Assert.Equal(70.0, widget.Y);
        surface.MoveWidgetTo(widget, -50, -50);
        Assert.Equal(0, widget.X);
        Assert.Equal(0, widget.Y);
    }

    [Fact]
    public void ResizeWidgetTo_Snaps_AndEnforcesMinimum()
    {
        var surface = NewSurface();
        surface.SnapStep = 8;
        var widget = AddWidget(surface);
        surface.ResizeWidgetTo(widget, 27, 3);
        Assert.Equal(24, widget.Width);
        Assert.Equal(24, widget.Height);
        surface.ResizeWidgetTo(widget, 70, 51);
        Assert.Equal(72, widget.Width);
        Assert.Equal(48, widget.Height);
    }

    [Fact]
    public void SnapEnabled_Toggle_DrivesSurfaceSnapStep()
    {
        var vm = new DesignerViewModel();
        try
        {
            Assert.True(vm.SnapEnabled);
            Assert.Equal(8, vm.Surface.SnapStep);
            vm.SnapEnabled = false;
            Assert.Null(vm.Surface.SnapStep);
            vm.SnapEnabled = true;
            Assert.Equal(8, vm.Surface.SnapStep);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void AddWidget_Undo_RestoresEmpty_Redo_ReAdds()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            Assert.Single(vm.Surface.Widgets);
            Assert.True(vm.CanUndo);

            vm.UndoCommand.Execute(null);
            Assert.Empty(vm.Surface.Widgets);
            Assert.True(vm.CanRedo);

            vm.RedoCommand.Execute(null);
            Assert.Single(vm.Surface.Widgets);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void DeleteSelected_Undo_RestoresWidget()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            vm.AddWidgetCommand.Execute(null);
            Assert.Equal(2, vm.Surface.Widgets.Count);

            // Select the second widget via the surface (as a click would).
            vm.Surface.SelectWidget(vm.Surface.Widgets[1]);
            Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
            vm.DeleteSelectedCommand.Execute(null);
            Assert.Single(vm.Surface.Widgets);

            vm.UndoCommand.Execute(null);
            Assert.Equal(2, vm.Surface.Widgets.Count);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void DuplicateSelected_OffsetsBy16()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            vm.Surface.SelectWidget(vm.Surface.Widgets[0]);
            vm.DuplicateSelectedCommand.Execute(null);
            Assert.Equal(2, vm.Surface.Widgets.Count);

            var first = vm.Surface.Widgets[0];
            var second = vm.Surface.Widgets[1];
            Assert.NotEqual(first.Id, second.Id);
            // Load orders by Z then Id — the copy may sort before the original.
            Assert.Equal(16, Math.Abs(second.X - first.X));
            Assert.Equal(16, Math.Abs(second.Y - first.Y));
            Assert.Equal(first.Type, second.Type);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void CopyThenPaste_AcrossSelection_Works()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            vm.Surface.SelectWidget(vm.Surface.Widgets[0]);
            vm.CopySelectedCommand.Execute(null);

            vm.Surface.SelectWidget(null);
            vm.PasteCommand.Execute(null);
            Assert.Equal(2, vm.Surface.Widgets.Count);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void AlignLeft_SetsXZero()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            vm.Surface.SelectWidget(vm.Surface.Widgets[0]);
            vm.AlignLeftCommand.Execute(null);
            Assert.Equal(0, vm.Surface.Widgets[0].X);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void AlignCenterX_CentersWithinCanvas()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            var widget = vm.Surface.Widgets[0];
            vm.Surface.SelectWidget(widget);
            vm.AlignCenterXCommand.Execute(null);
            Assert.Equal(Math.Round((vm.Surface.CanvasWidth - widget.Width) / 2), widget.X);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void Nudge_ClampsAtCanvasEdge()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            var widget = vm.Surface.Widgets[0];
            vm.Surface.SelectWidget(widget);
            vm.Nudge(-1000, -1000);
            Assert.Equal(0, widget.X);
            Assert.Equal(0, widget.Y);
            vm.Nudge(10000, 10000);
            Assert.Equal(vm.Surface.CanvasWidth - widget.Width, widget.X);
            Assert.Equal(vm.Surface.CanvasHeight - widget.Height, widget.Y);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void SelectedText_Edit_UpdatesLabelProp()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.SelectedPaletteType = "label";
            vm.AddWidgetCommand.Execute(null);
            var widget = vm.Surface.Widgets[0];
            vm.Surface.SelectWidget(widget);

            Assert.True(vm.SelectedIsTextLabel);
            vm.SelectedText = "Boiler Pressure";
            Assert.Equal("Boiler Pressure", widget.Props["text"].GetString());
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void SelectedBinding_Edit_BindsAndUnbinds()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null); // numeric default
            var widget = vm.Surface.Widgets[0];
            vm.Surface.SelectWidget(widget);

            vm.StagingBindingBridgeId = "b1";
            vm.StagingBindingSourceId = "s1";
            vm.StagingBindingDaItemId = "item1";
            vm.ApplySelectedBindingCommand.Execute(null);
            Assert.NotNull(widget.Binding);
            Assert.Equal("b1", widget.Binding!.Value.BridgeId);
            Assert.Equal("item1", widget.Binding.Value.DaItemId);

            // Clearing everything and applying unbinds.
            vm.StagingBindingBridgeId = string.Empty;
            vm.StagingBindingSourceId = string.Empty;
            vm.StagingBindingDaItemId = string.Empty;
            vm.ApplySelectedBindingCommand.Execute(null);
            Assert.Null(widget.Binding);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void RaiseZ_LowerZ_ChangesZWithinBounds()
    {
        var vm = new DesignerViewModel();
        try
        {
            vm.AddWidgetCommand.Execute(null);
            var widget = vm.Surface.Widgets[0];
            vm.Surface.SelectWidget(widget);

            vm.RaiseZCommand.Execute(null);
            Assert.Equal(1, widget.Z);
            vm.LowerZCommand.Execute(null);
            vm.LowerZCommand.Execute(null);
            Assert.Equal(0, widget.Z);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void UndoStack_IsBounded()
    {
        var vm = new DesignerViewModel();
        try
        {
            for (int i = 0; i < 60; i++)
            {
                vm.AddWidgetCommand.Execute(null);
            }

            int undoSteps = 0;
            while (vm.CanUndo)
            {
                vm.UndoCommand.Execute(null);
                undoSteps++;
                if (undoSteps > 55)
                {
                    Assert.Fail("Undo stack exceeded limit");
                }
            }

            Assert.Equal(50, undoSteps);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
