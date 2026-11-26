using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VISM;

// GTK owns the window and dialogs. WebKit hosts the same bundled page as WebView2.
sealed class LinuxHost(bool test, string? saveDirectory) : IDesktopHost
{
    readonly string page = new Uri(Path.Combine(AppContext.BaseDirectory, "ui", "index.html")).AbsoluteUri;
    readonly List<Delegate> signalHandlers = [];
    readonly int uiThread = Environment.CurrentManagedThreadId;
    SaveManager controller = null!;
    IntPtr window;
    IntPtr view;
    bool handlingMessage;
    bool closed;

    public int Run()
    {
        controller = new SaveManager(this, test, saveDirectory);
        try
        {
            if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
                throw new InvalidOperationException("A graphical Wayland or X11 session is required.");
            if (webkit_get_major_version() < 2 || (webkit_get_major_version() == 2 && webkit_get_minor_version() < 40))
                throw new InvalidOperationException("WebKitGTK 4.1 version 2.40 or newer is required.");
        }
        catch (DllNotFoundException error)
        {
            throw new InvalidOperationException("Install GTK3 and WebKitGTK 4.1 to launch the Linux build.", error);
        }

        window = gtk_window_new(0);
        gtk_window_set_title(window, test ? "VI Save Manager (test profile)" : "VI Save Manager");
        gtk_window_set_default_size(window, 980, 820);
        gtk_widget_set_size_request(window, 720, 560);
        gtk_window_set_position(window, 1); // GTK_WIN_POS_CENTER
        gtk_window_set_icon_from_file(window, Path.Combine(AppContext.BaseDirectory, "ui", "vism.png"), IntPtr.Zero);
        Connect(window, "destroy", new DestroyCallback((_, _) => { closed = true; gtk_main_quit(); }));

        var context = webkit_web_context_new_ephemeral();
        view = webkit_web_view_new_with_context(context);
        g_object_unref(context);
        // This local interface needs no GPU compositing. Software rendering also
        // works on X11 sessions without DRI3 and in remote desktops.
        webkit_settings_set_hardware_acceleration_policy(webkit_web_view_get_settings(view), 2);
        gtk_container_add(window, view);
        Connect(view, "context-menu", new ContextMenuCallback((_, _, _, _, _) => 1));
        Connect(view, "decide-policy", new PolicyCallback(DecidePolicy));
        Connect(view, "load-failed", new LoadFailedCallback((_, _, _, _, _) =>
        {
            Console.Error.WriteLine("VI Save Manager: could not load the bundled interface.");
            return 0;
        }));
        var manager = webkit_web_view_get_user_content_manager(view);
        Connect(manager, "script-message-received::vism", new MessageCallback(Receive));
        if (webkit_user_content_manager_register_script_message_handler(manager, "vism") == 0)
            throw new InvalidOperationException("Could not register the WebKit message channel.");

        gtk_widget_show_all(window);
        webkit_web_view_load_uri(view, page);
        gtk_main();
        return 0;
    }

    void Connect<TDelegate>(IntPtr instance, string signal, TDelegate handler) where TDelegate : Delegate
    {
        signalHandlers.Add(handler);
        g_signal_connect_data(instance, signal, Marshal.GetFunctionPointerForDelegate(handler), IntPtr.Zero, IntPtr.Zero, 0);
    }

    int DecidePolicy(IntPtr webView, IntPtr decision, int type, IntPtr data)
    {
        if (type == 0) // WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION
        {
            var action = webkit_navigation_policy_decision_get_navigation_action(decision);
            var request = webkit_navigation_action_get_request(action);
            if (Marshal.PtrToStringUTF8(webkit_uri_request_get_uri(request)) == page) return 0;
        }
        else if (type == 2) // WEBKIT_POLICY_DECISION_TYPE_RESPONSE
        {
            return 0;
        }
        webkit_policy_decision_ignore(decision);
        return 1;
    }

    void Receive(IntPtr manager, IntPtr result, IntPtr data)
    {
        if (closed || handlingMessage || Marshal.PtrToStringUTF8(webkit_web_view_get_uri(view)) != page) return;
        var value = webkit_javascript_result_get_js_value(result);
        var text = jsc_value_to_string(value);
        if (text == IntPtr.Zero) return;
        handlingMessage = true;
        try
        {
            if (Marshal.PtrToStringUTF8(text) is string json) controller.OnMessage(json);
        }
        catch (Exception error) { Console.Error.WriteLine("VI Save Manager: " + error.Message); }
        finally
        {
            handlingMessage = false;
            g_free(text);
        }
    }

    public void Post(string json)
    {
        void Deliver()
        {
            if (!closed)
                webkit_web_view_evaluate_javascript(view, $"window.vismReceive?.({json});", -1,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        if (Environment.CurrentManagedThreadId == uiThread) Deliver();
        else g_idle_add(DeliverOnUiThread, GCHandle.ToIntPtr(GCHandle.Alloc((Action)Deliver)));
    }

    static readonly IdleCallback DeliverOnUiThread = data =>
    {
        var handle = GCHandle.FromIntPtr(data);
        try { ((Action)handle.Target!).Invoke(); }
        finally { handle.Free(); }
        return 0;
    };

    public string? ChooseExport(string filename) => ChooseFiles("Share this save as a zip", 1,
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), filename).FirstOrDefault();

    public string[] ChooseImports() => ChooseFiles("Import GTA VI saves", 0, multi: true);

    public string? ChooseSaveFolder(string? current) => ChooseFiles("Select the VIctory saves folder", 2,
        current ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).FirstOrDefault();

    public void RevealExport(string path)
    {
        if (Path.GetDirectoryName(path) is not string folder) return;
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
    }

    string[] ChooseFiles(string title, int action, string? directory = null, string? filename = null, bool multi = false)
    {
        var dialog = gtk_file_chooser_native_new(title, window, action,
            action == 1 ? "Save" : action == 2 ? "Select" : "Open", "Cancel");
        try
        {
            gtk_file_chooser_set_select_multiple(dialog, multi ? 1 : 0);
            if (action == 2) gtk_file_chooser_set_show_hidden(dialog, 1);
            if (directory != null && Directory.Exists(directory)) gtk_file_chooser_set_current_folder(dialog, directory);
            if (filename != null)
            {
                gtk_file_chooser_set_current_name(dialog, filename);
                gtk_file_chooser_set_do_overwrite_confirmation(dialog, 1);
            }
            if (action != 2)
            {
                var filter = gtk_file_filter_new();
                gtk_file_filter_set_name(filter, "Save archives (*.zip)");
                gtk_file_filter_add_pattern(filter, "*.[zZ][iI][pP]");
                gtk_file_chooser_add_filter(dialog, filter);
            }
            if (gtk_native_dialog_run(dialog) != -3) return []; // GTK_RESPONSE_ACCEPT
            var list = gtk_file_chooser_get_filenames(dialog);
            var files = new List<string>();
            try
            {
                for (var item = list; item != IntPtr.Zero; item = Marshal.ReadIntPtr(item, IntPtr.Size))
                {
                    var file = Marshal.ReadIntPtr(item);
                    try { if (Marshal.PtrToStringUTF8(file) is string path) files.Add(path); }
                    finally { g_free(file); }
                }
            }
            finally { g_slist_free(list); }
            return files.ToArray();
        }
        finally { g_object_unref(dialog); }
    }

    const string Gtk = "libgtk-3.so.0";
    const string WebKit = "libwebkit2gtk-4.1.so.0";
    const string JavaScriptCore = "libjavascriptcoregtk-4.1.so.0";
    const string GLib = "libglib-2.0.so.0";
    const string GObject = "libgobject-2.0.so.0";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyCallback(IntPtr widget, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void MessageCallback(IntPtr manager, IntPtr result, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PolicyCallback(IntPtr view, IntPtr decision, int type, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ContextMenuCallback(IntPtr view, IntPtr menu, IntPtr ev, IntPtr hit, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadFailedCallback(IntPtr view, int ev, IntPtr uri, IntPtr error, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdleCallback(IntPtr data);

    [DllImport(Gtk)] static extern int gtk_init_check(IntPtr argc, IntPtr argv);
    [DllImport(Gtk)] static extern void gtk_main();
    [DllImport(Gtk)] static extern void gtk_main_quit();
    [DllImport(Gtk)] static extern IntPtr gtk_window_new(int type);
    [DllImport(Gtk)] static extern void gtk_window_set_title(IntPtr window, string title);
    [DllImport(Gtk)] static extern void gtk_window_set_default_size(IntPtr window, int width, int height);
    [DllImport(Gtk)] static extern void gtk_window_set_position(IntPtr window, int position);
    [DllImport(Gtk)] static extern int gtk_window_set_icon_from_file(IntPtr window, string filename, IntPtr error);
    [DllImport(Gtk)] static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);
    [DllImport(Gtk)] static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Gtk)] static extern void gtk_container_add(IntPtr container, IntPtr widget);
    [DllImport(Gtk)] static extern IntPtr gtk_file_chooser_native_new(string title, IntPtr parent, int action, string acceptLabel, string cancelLabel);
    [DllImport(Gtk)] static extern void gtk_file_chooser_set_select_multiple(IntPtr chooser, int value);
    [DllImport(Gtk)] static extern void gtk_file_chooser_set_show_hidden(IntPtr chooser, int value);
    [DllImport(Gtk)] static extern int gtk_file_chooser_set_current_folder(IntPtr chooser, string folder);
    [DllImport(Gtk)] static extern void gtk_file_chooser_set_current_name(IntPtr chooser, string name);
    [DllImport(Gtk)] static extern void gtk_file_chooser_set_do_overwrite_confirmation(IntPtr chooser, int value);
    [DllImport(Gtk)] static extern IntPtr gtk_file_filter_new();
    [DllImport(Gtk)] static extern void gtk_file_filter_set_name(IntPtr filter, string name);
    [DllImport(Gtk)] static extern void gtk_file_filter_add_pattern(IntPtr filter, string pattern);
    [DllImport(Gtk)] static extern void gtk_file_chooser_add_filter(IntPtr chooser, IntPtr filter);
    [DllImport(Gtk)] static extern int gtk_native_dialog_run(IntPtr dialog);
    [DllImport(Gtk)] static extern IntPtr gtk_file_chooser_get_filenames(IntPtr chooser);
    [DllImport(WebKit)] static extern uint webkit_get_major_version();
    [DllImport(WebKit)] static extern uint webkit_get_minor_version();
    [DllImport(WebKit)] static extern IntPtr webkit_web_context_new_ephemeral();
    [DllImport(WebKit)] static extern IntPtr webkit_web_view_new_with_context(IntPtr context);
    [DllImport(WebKit)] static extern IntPtr webkit_web_view_get_settings(IntPtr view);
    [DllImport(WebKit)] static extern void webkit_settings_set_hardware_acceleration_policy(IntPtr settings, int policy);
    [DllImport(WebKit)] static extern IntPtr webkit_web_view_get_user_content_manager(IntPtr view);
    [DllImport(WebKit)] static extern int webkit_user_content_manager_register_script_message_handler(IntPtr manager, string name);
    [DllImport(WebKit)] static extern IntPtr webkit_javascript_result_get_js_value(IntPtr result);
    [DllImport(WebKit)] static extern void webkit_web_view_load_uri(IntPtr view, string uri);
    [DllImport(WebKit)] static extern IntPtr webkit_web_view_get_uri(IntPtr view);
    [DllImport(WebKit)] static extern void webkit_web_view_evaluate_javascript(IntPtr view, string script, nint length, IntPtr world, IntPtr uri, IntPtr cancellation, IntPtr callback, IntPtr data);
    [DllImport(WebKit)] static extern IntPtr webkit_navigation_policy_decision_get_navigation_action(IntPtr decision);
    [DllImport(WebKit)] static extern IntPtr webkit_navigation_action_get_request(IntPtr action);
    [DllImport(WebKit)] static extern IntPtr webkit_uri_request_get_uri(IntPtr request);
    [DllImport(WebKit)] static extern void webkit_policy_decision_ignore(IntPtr decision);
    [DllImport(JavaScriptCore)] static extern IntPtr jsc_value_to_string(IntPtr value);
    [DllImport(GLib)] static extern void g_free(IntPtr data);
    [DllImport(GLib)] static extern void g_slist_free(IntPtr list);
    [DllImport(GLib)] static extern uint g_idle_add(IdleCallback callback, IntPtr data);
    [DllImport(GObject)] static extern void g_object_unref(IntPtr obj);
    [DllImport(GObject)] static extern nuint g_signal_connect_data(IntPtr obj, string signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
}
