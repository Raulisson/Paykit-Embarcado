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
    private int _controleAtivo = 0;   // NSU da transação em andamento
    private int _ultimoControle = 0;   // NSU da última transação aprovada (para desfazimento manual)
    private System.Threading.CancellationTokenSource? _cancelTokenSource = null;
    private decimal _ultimoValor = 0m;
    private int _ultimoCupom = 0;
    private bool _transacaoEmAndamento = false;

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
                case "cancelar":
                    await CancelarTransacaoAtiva();
                    break;

                case "desfazer":
                    await DesfazerTransacaoEmAndamento();
                    break;
                case "cancelar_aprovada":
                    await CancelarUltimaAprovada();
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
        _cancelTokenSource = new System.Threading.CancellationTokenSource();
        var cancelToken = _cancelTokenSource.Token;

        await ExecJs($"window.receberResultado({{status:'processando'," +
                     $"mensagem:'Iniciando {tipo} de R$ {valor:F2}. Use o pinpad...'}})");

        // Notifica o JS que pode exibir botão de cancelar
        await ExecJs("window.receberResultado({status:'aguardando_cartao',mensagem:'Aguardando cartão...'})");

        // Timeout de 2 minutos
        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2), cancelToken);

        _transacaoEmAndamento = true;
        // Transação em background
        var transacaoTask = Task.Run(() =>
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
        }, cancelToken);

        // Dispara o timeout em paralelo
        _ = Task.Run(async () =>
        {
            try
            {
                await timeoutTask;
                if (!transacaoTask.IsCompleted)
                {
                    Console.WriteLine("[Paykit] Timeout de 2 minutos atingido. Cancelando...");
                    _ger.MarcarCancelado();
                }
            }
            catch (TaskCanceledException) { /* transação terminou antes do timeout */ }
        });

        // Aguarda a transação terminar
        var (res, ctrl) = await transacaoTask;

        // Cancela o timeout se ainda estiver rodando
        _cancelTokenSource.Cancel();
        _ger.LimparCancelamento();
        _controleAtivo = ctrl;

        Console.WriteLine($"[Paykit] Transação resultado={res} controle={ctrl}");

        // Notifica JS que o botão de cancelar pode ser removido
        await ExecJs("window.receberResultado({status:'processando',mensagem:'Processando resposta...'})");

        if (res == 0)
        {
            _ultimoControle = ctrl; // guarda para eventual desfazimento manual
            _ultimoValor = valor;
            _ultimoCupom = cupom;
            await Task.Run(() =>
            {
                _tef.ConfirmaCartao(ctrl);
                _tef.FinalizaTransacao();
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
            _ultimoControle = 0; // limpa — não houve aprovação
            var erro = _tef.ObtemUltimoErro();

            await Task.Run(() =>
            {
                _tef.DesfazCartao(ctrl);
                _tef.FinalizaTransacao();
            });

            // Distingue cancelamento manual de recusa real
            string motivo = _ger.OperacaoCancelada() == 1 || erro.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                ? "Operação cancelada."
                : $"Recusado (código {res}). {Esc(erro)}";

            await ExecJs($"window.receberResultado({{status:'recusado',mensagem:'{motivo}'}})");
        }
        _transacaoEmAndamento = false;
        _controleAtivo = 0;
    }

    // Helpers
    private Task ExecJs(string js)
    {
        Console.WriteLine($"[C#→JS] {js[..Math.Min(js.Length, 120)]}");
        return _wv.CoreWebView2.ExecuteScriptAsync(js);
    }
    /// <summary>
    /// Cancela a transação em andamento sinalizando ao Paykit via OperacaoCancelada.
    /// </summary>
    private async Task CancelarTransacaoAtiva()
    {
        if (_tef == null || _controleAtivo == 0 && _ger == null)
        {
            await ExecJs("window.receberResultado({status:'erro',mensagem:'Nenhuma transação ativa para cancelar.'})");
            return;
        }

        Console.WriteLine("[MainForm] Cancelamento solicitado pelo operador.");
        _ger.MarcarCancelado();

        await ExecJs("window.receberResultado({status:'processando',mensagem:'Cancelando operação...'})");
    }

    /// Desfaz a transação em andamento (pinpad aguardando ou aprovada no ciclo atual).
    private async Task DesfazerTransacaoEmAndamento()
    {
        if (_tef == null) return;

        if (_transacaoEmAndamento)
        {
            // Ainda aguardando o pinpad — sinaliza cancelamento
            Console.WriteLine("[MainForm] Desfazimento: sinalizando cancelamento ao Paykit.");
            _ger.MarcarCancelado();
            await ExecJs("window.receberResultado({status:'processando',mensagem:'Cancelando operação no pinpad...'})");
            // O ProcessarPagamento vai perceber o retorno 11 e chamar DesfazCartao + FinalizaTransacao
            return;
        }

        // Não há transação ativa no momento
        await ExecJs("window.receberResultado({status:'erro',mensagem:'Nenhuma transação em andamento para desfazer.'})");
    }

    /// Cancela (estorna) a última transação já confirmada e finalizada.
    private async Task CancelarUltimaAprovada()
    {
        if (_tef == null)
        {
            await ExecJs("window.receberResultado({status:'erro',mensagem:'Paykit não inicializado.'})");
            return;
        }
        if (_ultimoControle == 0)
        {
            await ExecJs("window.receberResultado({status:'erro',mensagem:'Nenhuma transação aprovada disponível para cancelamento.'})");
            return;
        }

        int ctrl = _ultimoControle;
        decimal val = _ultimoValor;
        int cupom = _ultimoCupom;
        _ultimoControle = 0; // limpa imediatamente para evitar duplo cancelamento

        Console.WriteLine($"[MainForm] Cancelamento (estorno) solicitado. NSU: {ctrl}");
        await ExecJs($"window.receberResultado({{status:'processando',mensagem:'Cancelando transação NSU {ctrl}...'}})");

        var (res, ctrlCancel) = await Task.Run(() =>
        {
            int c = 0;
            int r = _tef.CancelarTransacaoAprovada(val, cupom, ctrl, out c);
            if (r == 0) _tef.ConfirmaCartao(c);
            else _tef.DesfazCartao(c);
            _tef.FinalizaTransacao();
            return (r, c);
        });

        if (res == 0)
            await ExecJs($"window.receberResultado({{status:'aprovado',mensagem:'Cancelamento do NSU {ctrl} realizado. NSU estorno: {ctrlCancel}'}})");
        else
        {
            var erro = _tef.ObtemUltimoErro();
            await ExecJs($"window.receberResultado({{status:'erro',mensagem:'Falha ao cancelar NSU {ctrl}. {Esc(erro)}'}})");
        }
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