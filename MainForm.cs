using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PaykitTotem.Paykit;
using System;
using System.IO;
using System.Diagnostics;
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

        Debug.WriteLine("[WebView2] Pronto.");
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
            Debug.WriteLine($"[Paykit] ConfigurarComunicacao → {rComm}");

            //Baixar certificado
            var certPath = Path.Combine(AppContext.BaseDirectory, "Bin", "certLinx.pem");
            int rCert = _tef.BuscarCertificado(null, certPath);
            Debug.WriteLine($"[Paykit] BuscarCertificado → {rCert} | {certPath}");

            if (rCert != 0)
                Debug.WriteLine("[Paykit] AVISO: falha ao baixar certificado. " +
                                  "Se já existir um na pasta Bin/, pode continuar.");

            var ver = _tef.VersaoDPOS();
            Debug.WriteLine($"[Paykit] Versão DLL: {ver}");

            // Abrir dia de movimento
            int r = _tef.InicializaDPOS();
            Debug.WriteLine($"[Paykit] InicializaDPOS → {r}");

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
        Debug.WriteLine($"[JS→C#] {json}");

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
                    await CancelarTransacaoPorCartao();
                    break;
                default:
                    await ExecJs($"window.receberResultado({{status:'erro',mensagem:'Ação desconhecida: {msg.Acao}'}})");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERRO OnJsMessage] {ex}");
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

        // Validação exigida pela SEFAZ para vendas acima de 10.000,00 sem identificação do consumidor.
        // Bloqueia o avanço para o Pinpad para evitar cobrança sem identificação fiscal
        if (valor >= 10000.00m)
        {
            await ExecJs("window.receberResultado({status:'erro',mensagem:'Valor limite excedido. Vendas a partir de R$ 10.000,00 exigem identificação do consumidor no caixa.'})");
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

        //  CAPTURA DO LOG JSON DO PAYKIT 
        string logJson = string.Empty;
        try
        {
            // Passa o controle (int) direto.
            logJson = _tef.ObtemLogTransacaoJson(ctrl);

            // Se falhar com o NSU, tenta buscar passando null (última transação geral)
            if (string.IsNullOrWhiteSpace(logJson))
            {
                logJson = _tef.ObtemLogTransacaoJson(null);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Paykit] Falha ao invocar ObtemLogTransacaoJson: {ex.Message}");
        }
        if (res == 0)
        {
            _ultimoControle = ctrl; // guarda para eventual desfazimento manual
            _ultimoValor = valor;
            _ultimoCupom = cupom;

                    Directory.CreateDirectory(pastaCupons);
                    Directory.CreateDirectory(pastaInterno);

                    var encodingAnsi = System.Text.Encoding.GetEncoding("Windows-1252");

                    int indexNsu = comp.IndexOf("NSU D-TEF", StringComparison.OrdinalIgnoreCase);

                        int indexFechaParentese = comp.IndexOf(")", indexNsu);

                        if (indexFechaParentese > 0)
                            //Separa as vias
                            string viaCliente = comp.Substring(0, pontoDeCorte).TrimEnd();
                            string viaEstabelecimento = comp.Substring(pontoDeCorte).TrimStart();

                            //Salva cada uma no seu respectivo diretório
                            File.WriteAllText(Path.Combine(pastaCupons, $"Via_Cliente_{ctrl}.txt"), viaCliente, encodingAnsi);
                            File.WriteAllText(Path.Combine(pastaInterno, $"Via_Loja_{ctrl}.txt"), viaEstabelecimento, encodingAnsi);
                    else
                    {
                        // Fallback de segurança: se o layout mudar drasticamente, salva o arquivo inteiro em ambos
                        File.WriteAllText(Path.Combine(pastaCupons, $"Cupom_{ctrl}.txt"), comp, encodingAnsi);
                        File.WriteAllText(Path.Combine(pastaInterno, $"Cupom_{ctrl}.txt"), comp, encodingAnsi);
                        Debug.WriteLine("[MainForm] Alerta: Não foi possível fatiar pelo NSU. Salvo cupom completo.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Erro Salvar TXT] {ex.Message}");
                }
            }

            // Confirma a operação
            await Task.Run(() =>
            {
                _tef.ConfirmaCartao(ctrl);
                _tef.FinalizaTransacao();
            });

            await ExecJs($"window.receberResultado({{" +
                         $"status:'aprovado'," +
                         $"mensagem:'Aprovado! NSU: {ctrl} | Cupom: {cupom}'}})");
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

            // SALVANDO O LOG JSON DA TRANSAÇÃO RECUSADA/CANCELADA
            if (!string.IsNullOrWhiteSpace(logJson))
            {
                try
                {
                    string pastaLogs = Path.Combine(AppContext.BaseDirectory, "Bin", "Cupons");
                    Directory.CreateDirectory(pastaLogs);
                    // Como pode não ter gerado NSU (ctrl = 0), usando o timestamp para não sobrepor
                    string sufixo = ctrl > 0 ? ctrl.ToString() : DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.WriteAllText(Path.Combine(pastaLogs, $"LogRecusado_{sufixo}.json"), logJson, System.Text.Encoding.UTF8);
                }
                catch (Exception ex) { Console.WriteLine($"[Erro Gravar JSON Recusado] {ex.Message}"); }
            }

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
        Debug.WriteLine($"[C#→JS] {js[..Math.Min(js.Length, 120)]}");
        return _wv.CoreWebView2.ExecuteScriptAsync(js);
    }

    /// Cancela a transação em andamento sinalizando ao Paykit via OperacaoCancelada
    private async Task CancelarTransacaoAtiva()
    {
        if (_tef == null || _controleAtivo == 0 && _ger == null)
        {
            await ExecJs("window.receberResultado({status:'erro',mensagem:'Nenhuma transação ativa para cancelar.'})");
            return;
        }

        Debug.WriteLine("[MainForm] Cancelamento solicitado pelo operador.");
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
            Debug.WriteLine("[MainForm] Desfazimento: sinalizando cancelamento ao Paykit.");
            _ger.MarcarCancelado();
            await ExecJs("window.receberResultado({status:'processando',mensagem:'Cancelando operação no pinpad...'})");
            // O ProcessarPagamento vai perceber o retorno 11 e chamar DesfazCartao + FinalizaTransacao
            return;
        }

        // Não há transação ativa no momento
        await ExecJs("window.receberResultado({status:'erro',mensagem:'Nenhuma transação em andamento para desfazer.'})");
    }


    /// Solicita o estorno direto pelo cartão do cliente (Identificação Automática no Pinpad).
    private async Task CancelarTransacaoPorCartao()
    {
        if (_tef == null)
        {
            await ExecJs("window.receberResultado({status:'erro',mensagem:'Paykit não inicializado.'})");
            return;
        }

        // Pegamos as variáveis da última venda como sugestão para inicializar a DLL
        int ctrlOriginal = _ultimoControle;
        decimal valOriginal = _ultimoValor;
        int cupomOriginal = _ultimoCupom;

        // Caso o operador tente clicar sem ter histórico em memória (fallback de segurança)
        if (ctrlOriginal == 0)
        {
            // Valores fictícios ou genéricos padrão apenas para abrir o canal da DLL
            ctrlOriginal = 1;
            valOriginal = 1.00m;
            cupomOriginal = 1;
        }

        Console.WriteLine($"[MainForm] Estorno assistido iniciado. Sugerido NSU: {ctrlOriginal}");
        await ExecJs($"window.receberResultado({{status:'processando',mensagem:'Insira ou aproxime o cartão utilizado na compra para processar o estorno...'}})");

        int ctrlCancel = 0;

        int res = await Task.Run(() =>
        {
            return _tef.CancelarTransacaoPorCartaoCompleta(valOriginal, cupomOriginal, ctrlOriginal, out ctrlCancel);
        });

        if (res == 0)
        {
            // Se a adquirente aceitou o cartão digitado/inserido e bateu com o histórico:
            Debug.WriteLine($"[MainForm] Estorno Autorizado! Controle do Estorno: {ctrlCancel}");

            var (comp, _) = _tef.ObtemComprovante(ctrlCancel);

            if (!string.IsNullOrWhiteSpace(comp))
            {
                await ExecJs($"window._comprovanteHandler('{Esc(comp)}')");

                try
                {
                    string pastaCupons = Path.Combine(AppContext.BaseDirectory, "bin", "Cupons");
                    string pastaInterno = Path.Combine(AppContext.BaseDirectory, "bin", "Interno");
                    Directory.CreateDirectory(pastaCupons);
                    Directory.CreateDirectory(pastaInterno);

                    var encodingAnsi = System.Text.Encoding.GetEncoding("Windows-1252");

                    int indexNsu = comp.IndexOf("NSU D-TEF", StringComparison.OrdinalIgnoreCase);

                    if (indexNsu > 0)
                    {
                        int indexFechaParentese = comp.IndexOf(")", indexNsu);

                        if (indexFechaParentese > 0)
                        {
                            int pontoDeCorte = indexFechaParentese + 1;

                            string viaCliente = comp.Substring(0, pontoDeCorte).TrimEnd();
                            string viaEstabelecimento = comp.Substring(pontoDeCorte).TrimStart();

                            File.WriteAllText(Path.Combine(pastaCupons, $"Via_Cliente_Estorno_{ctrlCancel}.txt"), viaCliente, encodingAnsi);
                            File.WriteAllText(Path.Combine(pastaInterno, $"Via_Loja_Estorno_{ctrlCancel}.txt"), viaEstabelecimento, encodingAnsi);

                            Debug.WriteLine($"[MainForm] Notas do estorno {ctrlCancel} geradas e fatiadas com sucesso.");
                        }
                    }
                    else
                    {
                        File.WriteAllText(Path.Combine(pastaCupons, $"Estorno_{ctrlCancel}.txt"), comp, encodingAnsi);
                        File.WriteAllText(Path.Combine(pastaInterno, $"Estorno_{ctrlCancel}.txt"), comp, encodingAnsi);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Erro Salvar Cupom Estorno] {ex.Message}");
                }
            }

            await Task.Run(() =>
            {
                _tef.ConfirmaCartao(ctrlCancel);
                _tef.FinalizaTransacao();
            });

            await ExecJs($"window.receberResultado({{status:'aprovado',mensagem:'Estorno efetuado com sucesso! Código: {ctrlCancel}'}})");
        }
        else
        {
            // Se deu erro ou o cliente desistiu
            await Task.Run(() =>
            {
                _tef.DesfazCartao(ctrlCancel);
                _tef.FinalizaTransacao();
            });

            var erro = _tef.ObtemUltimoErro();
            await ExecJs($"window.receberResultado({{status:'erro',mensagem:'Falha no estorno. {Esc(erro)}'}})");
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