using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PaykitTotem.Paykit;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PaykitTotem;

public partial class MainForm : Form
{
    private WebView2 _wv = null!;
    private IntegracaoPaykit _tef = null!;
    private GerenciadorTotem _ger = null!;
    private int _cupom = 1;

    public MainForm() => InitializeComponent();

    // Carregamento
    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await InitWebView();
        InitPaykit();
    }

    // WebView2
    private async Task InitWebView()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "WV2Data");
        Directory.CreateDirectory(dataDir);

        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await _wv.EnsureCoreWebView2Async(env);

        // Modo kiosk — desabilita menu de contexto e atalhos do browser
        _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _wv.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _wv.CoreWebView2.Settings.IsStatusBarEnabled = false;

        // Injeta as funções de bridge ANTES do HTML carregar
        await _wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
            // C# chama isso para enviar eventos do Paykit para a página
            window.paykitEvent = function(tipo, dados) {
                console.log('[paykitEvent]', tipo, dados);
                if (typeof window._paykitHandler === 'function')
                    window._paykitHandler(tipo, dados);
            };

            // C# chama isso com o resultado final (aprovado/recusado)
            window.receberResultado = function(obj) {
                console.log('[receberResultado]', obj);
                if (typeof window._resultadoHandler === 'function')
                    window._resultadoHandler(obj);
            };

            // C# chama isso com o texto do comprovante
            window.receberComprovante = function(texto) {
                if (typeof window._comprovanteHandler === 'function')
                    window._comprovanteHandler(texto);
            };

            // JS chama isso para enviar comandos ao C#
            window.enviarAoPaykit = function(json) {
                window.chrome.webview.postMessage(json);
            };
        """);

        // Recebe mensagens do JS
        _wv.CoreWebView2.WebMessageReceived += OnJsMessage;

        // Carrega o HTML
        var html = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        _wv.CoreWebView2.Navigate("file:///" + html.Replace('\\', '/'));

        Console.WriteLine("[WebView2] Pronto.");
    }

    // Paykit
    private void InitPaykit()
    {
        var binDir = Path.Combine(AppContext.BaseDirectory, "Bin");
        var dllPath = Path.Combine(binDir, "DPOSDRV.dll");

        if (!File.Exists(dllPath))
        {
            MessageBox.Show(
                $"DPOSDRV.dll não encontrada em:\n{dllPath}\n\n" +
                "Copie toda a pasta Bin/ do Paykit para a pasta de output do projeto.",
                "Paykit — DLL ausente", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Adiciona Bin/ ao PATH para as DLLs dependentes serem encontradas
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Contains(binDir, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", binDir + ";" + path);

        _tef = new IntegracaoPaykit();
        _tef.SetDllPath(dllPath);
        _tef.SetKeepLoaded(true);  // mantém a DLL carregada entre transações

        _ger = new GerenciadorTotem(js => _wv.CoreWebView2.ExecuteScriptAsync(js));
        _tef.SetGerenciador(_ger);

        try
        {
            _tef.IdentificacaoAC("PaykitTotem", "1.0.0");

            // Configura modo de desfazimento
            _tef.ConfiguraModoDesfazimento(1);
            _tef.ConfigurarCNPJ("70895669000174");
            _tef.ConfigurarEmpresaLojaPDV(empresa: 1, loja: 558, pdv: 1);
            int rComm = _tef.ConfigurarComunicacao("tef-stlb01.linxsaas.com.br:8778:1");
            Console.WriteLine($"[Paykit] ConfigurarComunicacao → {rComm}");

            //Baixar certificado
            var certPath = Path.Combine(AppContext.BaseDirectory, "Bin", "certLinx.pem");
            int rCert = _tef.BuscarCertificado(null, certPath);
            Console.WriteLine($"[Paykit] BuscarCertificado → {rCert} | {certPath}");

            if (rCert != 0)
                Console.WriteLine("[Paykit] AVISO: falha ao baixar certificado. " +
                                  "Se já existir um na pasta Bin/, pode continuar.");

            var ver = _tef.VersaoDPOS();
            Console.WriteLine($"[Paykit] Versão DLL: {ver}");

            // Abrir dia de movimento
            int r = _tef.InicializaDPOS();
            Console.WriteLine($"[Paykit] InicializaDPOS → {r}");

            if (r != 0)
                MessageBox.Show(
                    $"InicializaDPOS retornou {r}.\n" +
                    "Verifique:\n" +
                    "• Conexão com internet ativa\n" +
                    "• Firewall não bloqueando tef-stlb01.linxsaas.com.br:8778\n" +
                    "• Certificado baixado corretamente",
                    "Paykit — Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Paykit ERRO] {ex}");
            MessageBox.Show(ex.Message, "Erro ao inicializar Paykit",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Mensagens do JavaScript para C#
    private async void OnJsMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        Console.WriteLine($"[JS→C#] {json}");

        try
        {
            var msg = JsonSerializer.Deserialize<JsMsg>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (msg == null) return;

            switch (msg.Acao?.ToLowerInvariant())
            {
                case "credito":
                    await ProcessarPagamento(msg.Valor ?? 0m, "credito");
                    break;
                case "debito":
                    await ProcessarPagamento(msg.Valor ?? 0m, "debito");
                    break;
                case "status":
                    await ExecJs($"window.receberResultado({{status:'ok',mensagem:'Paykit ativo.'}})");
                    break;
                case "pix":
                    await ProcessarPagamento(msg.Valor ?? 0m, "pix");
                    break;
                default:
                    await ExecJs($"window.receberResultado({{status:'erro',mensagem:'Ação desconhecida: {msg.Acao}'}})");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO OnJsMessage] {ex}");
            await ExecJs($"window.receberResultado({{status:'erro',mensagem:'{Esc(ex.Message)}'}})");
        }
    }

    // Fluxo de pagamento
    private async Task ProcessarPagamento(decimal valor, string tipo)
    {
        if (_tef == null)
        {
            await ExecJs("window.receberResultado({status:'erro',mensagem:'Paykit não inicializado.'})");
            return;
        }

        int cupom = _cupom++;

        await ExecJs($"window.receberResultado({{status:'processando'," +
                     $"mensagem:'Iniciando {tipo} de R$ {valor:F2}. Use o pinpad...'}})");

        var (res, ctrl) = await Task.Run(() =>
        {
            int c = 0;
            int r = tipo switch
            {
                "credito" => _tef.TransacaoCreditoCompleta(valor, cupom, out c, "AV", 1),
                "debito" => _tef.TransacaoDebitoCompleta(valor, cupom, out c),
                "pix" => _tef.TransacaoQRCode(valor, cupom, out c),
                _ => throw new Exception($"Tipo desconhecido: {tipo}")
            };
            return (r, c);
        });

        Console.WriteLine($"[Paykit] Transação resultado={res} controle={ctrl}");

        if (res == 0)
        {
            // APROVADO
            await Task.Run(() =>
            {
                _tef.ConfirmaCartao(ctrl);   // confirmar com a instituição financeira
                _tef.FinalizaTransacao();     // encerrar ciclo
            });

            var (comp, _) = _tef.ObtemComprovante(ctrl);

            await ExecJs($"window.receberResultado({{" +
                         $"status:'aprovado'," +
                         $"mensagem:'Aprovado! NSU: {ctrl} | Cupom: {cupom}'}})");

            if (!string.IsNullOrWhiteSpace(comp))
                await ExecJs($"window.receberComprovante('{Esc(comp)}')");
        }
        else
        {
            // RECUSADO ou ERRO
            var erro = _tef.ObtemUltimoErro();

            await Task.Run(() =>
            {
                _tef.DesfazCartao(ctrl);     // desfazer junto à instituição financeira
                _tef.FinalizaTransacao();    // encerrar ciclo
            });

            await ExecJs($"window.receberResultado({{" +
                         $"status:'recusado'," +
                         $"mensagem:'Recusado (código {res}). {Esc(erro)}'}})");
        }
    }

    // Helpers
    private Task ExecJs(string js)
    {
        Console.WriteLine($"[C#→JS] {js[..Math.Min(js.Length, 120)]}");
        return _wv.CoreWebView2.ExecuteScriptAsync(js);
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'")
         .Replace("\r", "").Replace("\n", "\\n");

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _tef?.FinalizaDPOS(); } catch { }
        _tef?.Dispose();
        base.OnFormClosing(e);
    }
}

// Modelo da mensagem JSON vinda do JavaScript
record JsMsg(string? Acao, decimal? Valor, string? Descricao);