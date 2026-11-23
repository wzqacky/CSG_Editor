# CSG Studio

Run the scene to use the runtime CSG workspace. Right-click the graph to add nodes and drag outputs to operation inputs.

Use Ctrl+Z and Ctrl+Y (or Cmd+Z and Cmd+Y on macOS) to undo and redo graph edits, or right-click the graph for the Undo and Redo menu. Ctrl+Shift+Z also redoes an edit. History retains up to 100 states.

New clears the graph and its custom shapes. It asks for confirmation when the canvas is not empty and cannot be undone.

Shift-click at least two node cards, then right-click the graph and choose **Create custom shape**. Name the shape to save a snapshot of the selected nodes and their connected inputs in the add menu. Adding it creates independent, editable node copies; its parts stay separate and initially overlap the original 3D positions. Changes to the originals do not change the snapshot. Use the x beside a custom shape in the menu to remove it. Custom shapes are included in saved states and undo history.

The top-bar Save, Import and Export OBJ buttons open a file chooser on Windows, macOS and Linux. Save writes the graph, node settings, positions, and connections as JSON; Import replaces the current graph and can be undone. Export OBJ writes the currently visible, complete preview meshes. The exported geometry includes positions and normals, but not preview colors or materials. Incomplete operations have no preview mesh; fix any preview errors before exporting. File errors are shown in the status bar and Unity console.

## Resources

- [Unity Simple File Browser](https://github.com/yasirkula/UnitySimpleFileBrowser/tree/v1.7.8) by yasirkula: runtime file chooser, installed via the Unity Package Manager ([MIT license](https://github.com/yasirkula/UnitySimpleFileBrowser/blob/v1.7.8/LICENSE.txt)).
- TextMesh Pro Essential Resources: imported from the Unity UI package's `Package Resources/TMP Essential Resources.unitypackage` into [Assets/TextMesh Pro](<Assets/TextMesh Pro>). The file browser uses these text assets.
- Liberation Sans: font included with the TextMesh Pro resources, under the [SIL Open Font License 1.1](<Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt>).
