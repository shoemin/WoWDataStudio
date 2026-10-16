using OpenTK.Graphics.OpenGL4;

namespace WoWDataStudio.Rendering;

/// <summary>
/// Counts the OpenGL objects the app's renderers create and delete (textures, buffers, vertex arrays, programs) and the
/// GL contexts of the 3D views (one per <see cref="Controls.ModelViewport"/>: GLWpfControl gives every view its own
/// context and Direct3D device). Opening and closing a window with 3D views must bring every count back to where it
/// was: the leak check of the pop-out windows (the statistics, the screenshot run and the tests read it).
/// All calls need the right GL context current (they run inside the renderers).
/// </summary>
public static class GlTracker
{
    private static int _textures, _buffers, _arrays, _programs, _contexts, _contextsOpened, _contextsClosed;

    public static int Textures => Volatile.Read(ref _textures);
    public static int Buffers => Volatile.Read(ref _buffers);
    public static int VertexArrays => Volatile.Read(ref _arrays);
    public static int Programs => Volatile.Read(ref _programs);
    /// <summary>GL contexts alive now (3D views started and not yet disposed).</summary>
    public static int Contexts => Volatile.Read(ref _contexts);
    public static int ContextsOpened => Volatile.Read(ref _contextsOpened);
    public static int ContextsClosed => Volatile.Read(ref _contextsClosed);

    /// <summary>Every live object (textures + buffers + vertex arrays + programs).</summary>
    public static int Objects => Textures + Buffers + VertexArrays + Programs;

    public static string Summary => $"{Contexts} GL contexts ({ContextsOpened} opened, {ContextsClosed} closed), {Textures} textures, {Buffers} buffers, {VertexArrays} vertex arrays, {Programs} programs";

    public static int GenTexture()
    {
        Interlocked.Increment(ref _textures);
        return GL.GenTexture();
    }

    public static void DeleteTexture(int id)
    {
        if (id == 0) return;
        Interlocked.Decrement(ref _textures);
        GL.DeleteTexture(id);
    }

    public static int GenBuffer()
    {
        Interlocked.Increment(ref _buffers);
        return GL.GenBuffer();
    }

    public static void DeleteBuffer(int id)
    {
        if (id == 0) return;
        Interlocked.Decrement(ref _buffers);
        GL.DeleteBuffer(id);
    }

    public static int GenVertexArray()
    {
        Interlocked.Increment(ref _arrays);
        return GL.GenVertexArray();
    }

    public static void DeleteVertexArray(int id)
    {
        if (id == 0) return;
        Interlocked.Decrement(ref _arrays);
        GL.DeleteVertexArray(id);
    }

    public static int CreateProgram()
    {
        Interlocked.Increment(ref _programs);
        return GL.CreateProgram();
    }

    public static void DeleteProgram(int id)
    {
        if (id == 0) return;
        Interlocked.Decrement(ref _programs);
        GL.DeleteProgram(id);
    }

    internal static void ContextOpened()
    {
        Interlocked.Increment(ref _contexts);
        Interlocked.Increment(ref _contextsOpened);
    }

    internal static void ContextClosed()
    {
        Interlocked.Decrement(ref _contexts);
        Interlocked.Increment(ref _contextsClosed);
    }
}
