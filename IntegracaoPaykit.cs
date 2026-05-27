using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PaykitTotem.Paykit;

// Interface dos callbacks sem telas
public interface IGerenciadorSemTelas
{
    // Mensagens de display
    void DisplayTerminal(string mensagem);
    void DisplayErro(string mensagem);
    void Mensagem(string mensagem);
    void MensagemAlerta(string mensagem);
    int MensagemAdicional(string mensagem);
    int ImagemAdicional(int indiceImagem);
    void PreviewComprovante(string comprovante);

    // Beep
    void Beep();

    // Confirmação simples (retorna 0=SIM, 1=NÃO)
    int SolicitaConfirmacao(string mensagem);

    // Entradas de dados
    int EntraCartao(string label, ref string numeroCartao);
    int EntraDataValidade(string label, ref string dataValidade);
    int EntraData(string label, ref string data);
    int EntraCodigoSeguranca(string label, ref string codigo, int tamanhoMax);
    int SelecionaOpcao(string label, string opcoes, ref int opcaoSelecionada);
    int EntraValor(string label, ref decimal valor, decimal min, decimal max);
    int EntraValorEspecial(string label, ref decimal valor, decimal min, decimal max, int casasDecimais);
    int EntraNumero(string label, ref string numero, int min, int max, int minDigitos, int maxDigitos, int digitosExatos);
    int EntraString(string label, ref string valor, string tamanhoMaximo);
    int EntraCodigoBarras(string label, ref string campo);
    int EntraCodigoBarrasLido(string label, ref string campo);

    // Controle de cancelamento
    int OperacaoCancelada();
    int SetaOperacaoCancelada(bool cancelada);
    void ProcessaMensagens();

    // Planos de pagamento
    int SelecionaPlanosEx(string solicitacao, ref string retorno);

    // Histórico
    int DadosHistorico(string parte1, int tamParte1, string parte2, int tamParte2);
    int Comandos(string dadosEntrada, ref string dadosRetorno);
}

// Wrapper da DPOSDRV.dll
public sealed class IntegracaoPaykit : IDisposable
{   
    #region ── Win32 ──────────────────────────────────────────────────────────
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string filename);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr handle);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr handle, string proc);
    #endregion

    #region ── Delegates de transação ─────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Del_VersaoDPOS(StringBuilder versao);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate int Del_TransacaoCancelamentoPagamentoCompleta(
        StringBuilder pValorTransacao,
        StringBuilder pNumeroCupomVenda,
        StringBuilder pNumeroControle,
        StringBuilder pPermiteAlteracao,
        StringBuilder pReservado
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int Del_TransacaoCancelamentoPagamento(StringBuilder pNumeroControle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfiguraComunicacao(StringBuilder config);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_BuscaCertificado(StringBuilder url, StringBuilder pathCert);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_InicializaDPOS();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_FinalizaDPOS();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfiguraDPOS();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfiguraModoDesfazimento(int modo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfiguraCNPJ(StringBuilder cnpj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfiguraEmpresaLojaPDV(StringBuilder empresa, StringBuilder loja, StringBuilder pdv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_IdentificacaoAC(StringBuilder nome, StringBuilder versao, StringBuilder reservado);

    // Transações
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_TransacaoCredito(StringBuilder valor, StringBuilder cupom, StringBuilder controle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_TransacaoQRCode(
        StringBuilder valor, StringBuilder cupom,
        StringBuilder controle, StringBuilder transactionParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_TransacaoCreditoCompleta(
        StringBuilder valor, StringBuilder cupom, StringBuilder controle,
        StringBuilder tipoOp, StringBuilder parcelas, StringBuilder valParcela,
        StringBuilder valTaxa, StringBuilder permiteAlt, byte[] reservado);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_TransacaoDebito(StringBuilder valor, StringBuilder cupom, StringBuilder controle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_TransacaoDebitoCompleta(
        StringBuilder valor, StringBuilder cupom, StringBuilder controle,
        StringBuilder tipoOp, StringBuilder parcelas, StringBuilder seqParcela,
        StringBuilder dataDebito, StringBuilder valParcela,
        StringBuilder valTaxa, StringBuilder permiteAlt, byte[] reservado);

    // Confirmação / desfazimento
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfirmaCartao(StringBuilder controle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfirmaCartaoCredito(StringBuilder controle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ConfirmaCartaoDebito(StringBuilder controle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_DesfazCartao(StringBuilder controle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_FinalizaTransacao();

    // Diagnóstico
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ObtemUltimoErro(byte[] erro);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate void Del_ObtemComprovante(string pNumeroControle, StringBuilder pCupomCompleto, StringBuilder pCupomReduzido);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Del_ProcuraPinPad(byte[] dados);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr Del_ObtemLogTransacaoJson(StringBuilder pNumeroControle);
    #endregion

    #region ── Delegates de callbacks "sem telas" ─────────────────────────────

    // DisplayTerminal
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_DisplayTerminal(Cb_DisplayTerminal cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cb_DisplayTerminal(StringBuilder msg);

    // DisplayErro
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_DisplayErro(Cb_DisplayErro cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cb_DisplayErro(StringBuilder msg);

    // Mensagem
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_Mensagem(Cb_Mensagem cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cb_Mensagem(StringBuilder msg);

    // Beep
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_Beep(Cb_Beep cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cb_Beep();

    // SolicitaConfirmacao
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_SolicitaConfirmacao(Cb_SolicitaConfirmacao cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_SolicitaConfirmacao(StringBuilder msg);

    // EntraCartao
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraCartao(Cb_EntraCartao cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraCartao(StringBuilder label, IntPtr cartao);

    // EntraDataValidade
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraDataValidade(Cb_EntraDataValidade cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraDataValidade(StringBuilder label, IntPtr data);

    // EntraData
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraData(Cb_EntraData cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraData(StringBuilder label, IntPtr data);

    // EntraCodigoSeguranca
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraCodigoSeguranca(Cb_EntraCodigoSeguranca cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraCodigoSeguranca(StringBuilder label, IntPtr codigo, int tamanhoMax);

    // SelecionaOpcao
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_SelecionaOpcao(Cb_SelecionaOpcao cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_SelecionaOpcao(StringBuilder label, StringBuilder opcoes, ref int opcao);

    // ntraValor
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraValor(Cb_EntraValor cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraValor(StringBuilder label, IntPtr valor, StringBuilder min, StringBuilder max);

    // EntraValorEspecial
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraValorEspecial(Cb_EntraValorEspecial cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraValorEspecial(StringBuilder label, IntPtr valor, StringBuilder parametros);

    // EntraNumero
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraNumero(Cb_EntraNumero cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraNumero(StringBuilder label, IntPtr numero,
        StringBuilder min, StringBuilder max, int minDig, int maxDig, int digExatos);

    // OperacaoCancelada
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_OperacaoCancelada(Cb_OperacaoCancelada cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_OperacaoCancelada();

    // SetaOperacaoCancelada
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_SetaOperacaoCancelada(Cb_SetaOperacaoCancelada cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_SetaOperacaoCancelada([MarshalAs(UnmanagedType.I1)] bool cancelada);

    // ProcessaMensagens
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_ProcessaMensagens(Cb_ProcessaMensagens cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cb_ProcessaMensagens();

    // EntraString
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraString(Cb_EntraString cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraString(StringBuilder label, IntPtr str, StringBuilder tamanho);

    // EntraCodigoBarras
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraCodigoBarras(Cb_EntraCodigoBarras cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraCodigoBarras(StringBuilder label, IntPtr campo);

    // EntraCodigoBarrasLido
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_EntraCodigoBarrasLido(Cb_EntraCodigoBarrasLido cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_EntraCodigoBarrasLido(StringBuilder label, IntPtr campo);

    // MensagemAlerta
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_MensagemAlerta(Cb_MensagemAlerta cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cb_MensagemAlerta(StringBuilder msg);

    // PreviewComprovante
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_PreviewComprovante(Cb_PreviewComprovante cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cb_PreviewComprovante(StringBuilder comp);

    // MensagemAdicional
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_MensagemAdicional(Cb_MensagemAdicional cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_MensagemAdicional(StringBuilder msg);

    // ImagemAdicional
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_ImagemAdicional(Cb_ImagemAdicional cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_ImagemAdicional(int indice);

    // SelecionaPlanosEx
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_SelecionaPlanosEx(Cb_SelecionaPlanosEx cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_SelecionaPlanosEx(StringBuilder solicitacao, IntPtr retorno);

    // Comandos (QR Code, etc.)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Reg_Comandos(Cb_Comandos cb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Cb_Comandos(StringBuilder dadosEntrada, IntPtr dadosRetorno);

    #endregion

    #region Estado interno 
    private IntPtr _handle = IntPtr.Zero;
    private IGerenciadorSemTelas? _gerenciador;
    private bool _keepLoaded = true;
    private string _dllPath = "DPOSDRV.dll";

    // GCHandles — mantém os delegates vivos para o GC não coletar
    private readonly List<GCHandle> _handles = new();
    #endregion

    // Configuração
    public void SetDllPath(string path) => _dllPath = path;
    public void SetKeepLoaded(bool v) => _keepLoaded = v;
    public void SetGerenciador(IGerenciadorSemTelas g) => _gerenciador = g;

    // Carga / descarga
    private void EnsureLoaded()
    {
        if (_handle != IntPtr.Zero) return;

        _handle = LoadLibrary(_dllPath);
        if (_handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new DllNotFoundException(
                $"Não foi possível carregar '{_dllPath}'. " +
                $"Erro Win32: {err}. " +
                $"Verifique: (1) a pasta Bin/ existe no output; (2) projeto compilado como x64.");
        }

        if (_gerenciador != null)
            RegisterCallbacks();
    }

    private void Unload()
    {
        if (_handle != IntPtr.Zero) { FreeLibrary(_handle); _handle = IntPtr.Zero; }
    }

    private void MaybeUnload() { if (!_keepLoaded) Unload(); }

    // Obtém um delegate tipado de uma função exportada pela DLL
    private T Fn<T>(string name) where T : Delegate
    {
        var ptr = GetProcAddress(_handle, name);
        if (ptr == IntPtr.Zero)
            throw new EntryPointNotFoundException(
                $"Função '{name}' não encontrada em '{_dllPath}'.");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    // Aloca um GCHandle para que o GC não colete o delegate de callback
    private T Pin<T>(T del) where T : Delegate
    {
        _handles.Add(GCHandle.Alloc(del));
        return del;
    }

    // Registro dos callbacks "sem telas"
    private void RegisterCallbacks()
    {
        var g = _gerenciador!;

        // DisplayTerminal
        Fn<Reg_DisplayTerminal>("RegPDVDisplayTerminal")(
            Pin<Cb_DisplayTerminal>(msg => g.DisplayTerminal(msg.ToString())));

        // DisplayErro
        Fn<Reg_DisplayErro>("RegPDVDisplayErro")(
            Pin<Cb_DisplayErro>(msg => g.DisplayErro(msg.ToString())));

        // Mensagem
        Fn<Reg_Mensagem>("RegPDVMensagem")(
            Pin<Cb_Mensagem>(msg => g.Mensagem(msg.ToString())));

        // Beep
        Fn<Reg_Beep>("RegPDVBeep")(
            Pin<Cb_Beep>(() => g.Beep()));

        // SolicitaConfirmacao
        Fn<Reg_SolicitaConfirmacao>("RegPDVSolicitaConfirmacao")(
            Pin<Cb_SolicitaConfirmacao>(msg => g.SolicitaConfirmacao(msg.ToString())));

        // MensagemAlerta
        Fn<Reg_MensagemAlerta>("RegPDVMensagemAlerta")(
            Pin<Cb_MensagemAlerta>(msg => g.MensagemAlerta(msg.ToString())));

        // PreviewComprovante
        Fn<Reg_PreviewComprovante>("RegPDVPreviewComprovante")(
            Pin<Cb_PreviewComprovante>(comp => g.PreviewComprovante(comp.ToString())));

        // MensagemAdicional
        Fn<Reg_MensagemAdicional>("RegPDVMensagemAdicional")(
            Pin<Cb_MensagemAdicional>(msg => g.MensagemAdicional(msg.ToString())));

        // ImagemAdicional
        Fn<Reg_ImagemAdicional>("RegPDVImagemAdicional")(
            Pin<Cb_ImagemAdicional>(idx => g.ImagemAdicional(idx)));

        // OperacaoCancelada
        Fn<Reg_OperacaoCancelada>("RegPDVOperacaoCancelada")(
            Pin<Cb_OperacaoCancelada>(() => g.OperacaoCancelada()));

        // SetaOperacaoCancelada
        Fn<Reg_SetaOperacaoCancelada>("RegPDVSetaOperacaoCancelada")(
            Pin<Cb_SetaOperacaoCancelada>(b => g.SetaOperacaoCancelada(b)));

        // ProcessaMensagens
        Fn<Reg_ProcessaMensagens>("RegPDVProcessaMensagens")(
            Pin<Cb_ProcessaMensagens>(() => g.ProcessaMensagens()));

        // EntraCartao
        Fn<Reg_EntraCartao>("RegPDVEntraCartao")(Pin<Cb_EntraCartao>((label, pCartao) =>
        {
            string s = "";
            int r = g.EntraCartao(label.ToString(), ref s);
            if (r == 0) WriteIntPtr(pCartao, s, 20);
            return r;
        }));

        // EntraDataValidade
        Fn<Reg_EntraDataValidade>("RegPDVEntraDataValidade")(Pin<Cb_EntraDataValidade>((label, pData) =>
        {
            string s = ReadIntPtr(pData, 5);
            int r = g.EntraDataValidade(label.ToString(), ref s);
            if (r == 0) WriteIntPtr(pData, s, 5);
            return r;
        }));

        // EntraData
        Fn<Reg_EntraData>("RegPDVEntraData")(Pin<Cb_EntraData>((label, pData) =>
        {
            string s = ReadIntPtr(pData, 9);
            int r = g.EntraData(label.ToString(), ref s);
            if (r == 0) WriteIntPtr(pData, s, 9);
            return r;
        }));

        // EntraCodigoSeguranca
        Fn<Reg_EntraCodigoSeguranca>("RegPDVEntraCodigoSeguranca")(
            Pin<Cb_EntraCodigoSeguranca>((label, pCod, tam) =>
            {
                string s = "";
                int r = g.EntraCodigoSeguranca(label.ToString(), ref s, tam);
                if (r == 0) WriteIntPtr(pCod, s, tam + 1);
                return r;
            }));

        // SelecionaOpcao
        Fn<Reg_SelecionaOpcao>("RegPDVSelecionaOpcao")(
            Pin<Cb_SelecionaOpcao>((label, opcoes, ref opcao) =>
                g.SelecionaOpcao(label.ToString(), opcoes.ToString(), ref opcao)));

        // EntraValor
        Fn<Reg_EntraValor>("RegPDVEntraValor")(Pin<Cb_EntraValor>((label, pVal, min, max) =>
        {
            decimal dMin = ToDecimal(min.ToString());
            decimal dMax = ToDecimal(max.ToString());
            decimal dVal = ToDecimal(ReadIntPtr(pVal, 12));
            int r = g.EntraValor(label.ToString(), ref dVal, dMin, dMax);
            if (r == 0) WriteIntPtr(pVal, FmtValor(12, dVal), 13);
            return r;
        }));

        // EntraValorEspecial
        Fn<Reg_EntraValorEspecial>("RegPDVEntraValorEspecial")(
            Pin<Cb_EntraValorEspecial>((label, pVal, param) =>
            {
                string p = param.ToString();
                int casas = p.Length > 24 ? (int.TryParse(p.Substring(24, 1), out int c) ? c : 2) : 2;
                decimal dVal = ToDecimal(ReadIntPtr(pVal, 12));
                decimal dMin = ToDecimal(p.Length >= 12 ? p[..12] : "0");
                decimal dMax = ToDecimal(p.Length >= 24 ? p.Substring(12, 12) : "0");
                int r = g.EntraValorEspecial(label.ToString(), ref dVal, dMin, dMax, casas);
                if (r == 0) WriteIntPtr(pVal, FmtValor(12, dVal), 13);
                return r;
            }));

        // EntraNumero
        Fn<Reg_EntraNumero>("RegPDVEntraNumero")(
            Pin<Cb_EntraNumero>((label, pNum, min, max, minD, maxD, digEx) =>
            {
                string s = ReadIntPtr(pNum, 13).TrimEnd('\0');
                int iMin = int.TryParse(min.ToString(), out int m1) ? m1 : 0;
                int iMax = int.TryParse(max.ToString(), out int m2) ? m2 : 0;
                int r = g.EntraNumero(label.ToString(), ref s, iMin, iMax, minD, maxD, digEx);
                if (r == 0) WriteIntPtr(pNum, s, 13);
                return r;
            }));

        // EntraString
        Fn<Reg_EntraString>("RegPDVEntraString")(Pin<Cb_EntraString>((label, pStr, tam) =>
        {
            string s = "";
            int r = g.EntraString(label.ToString(), ref s, tam.ToString());
            if (r == 0)
            {
                int maxLen = int.TryParse(tam.ToString(), out int t) ? t : 64;
                WriteIntPtr(pStr, s, maxLen + 1);
            }
            return r;
        }));

        // EntraCodigoBarras
        Fn<Reg_EntraCodigoBarras>("RegPDVEntraCodigoBarras")(Pin<Cb_EntraCodigoBarras>((label, pCod) =>
        {
            string s = "";
            int r = g.EntraCodigoBarras(label.ToString(), ref s);
            if (r == 0) WriteIntPtr(pCod, s, 50);
            return r;
        }));

        // EntraCodigoBarrasLido
        Fn<Reg_EntraCodigoBarrasLido>("RegPDVEntraCodigoBarrasLido")(
            Pin<Cb_EntraCodigoBarrasLido>((label, pCod) =>
            {
                string s = "";
                int r = g.EntraCodigoBarrasLido(label.ToString(), ref s);
                if (r == 0) WriteIntPtr(pCod, s, 50);
                return r;
            }));

        // SelecionaPlanosEx
        Fn<Reg_SelecionaPlanosEx>("RegPDVSelecionaPlanosEx")(
            Pin<Cb_SelecionaPlanosEx>((sol, pRet) =>
            {
                string ret = "";
                int r = g.SelecionaPlanosEx(sol.ToString(), ref ret);
                if (r == 0) WriteIntPtr(pRet, ret, ret.Length + 1);
                return r;
            }));

        // Comandos (QR Code etc.)
        try
        {
            Fn<Reg_Comandos>("RegPDVComandos")(Pin<Cb_Comandos>((dados, pRet) =>
            {
                string retorno = "";
                int r = g.Comandos(dados.ToString(), ref retorno);
                if (r == 0) WriteIntPtr(pRet, retorno, retorno.Length + 1);
                return r;
            }));
        }
        catch (EntryPointNotFoundException)
        {
            // Versão da DLL não exporta RegPDVComandos — ok, ignora
        }
    }

    // Helpers de marshal / formatação

    // Valor monetário Paykit: sem vírgula, 2 decimais implícitos
    // Ex: 45.90 → "000000004590"
    public static string FmtValor(int digits, decimal val) =>
        string.Format("{0:d" + digits + "}", (long)(val * 100m));

    public static string FmtInt(int digits, int val) =>
        string.Format("{0:d" + digits + "}", val);

    // "000000004590" → 45.90m
    private static decimal ToDecimal(string s) =>
        decimal.TryParse(s.Trim('\0', ' '), out decimal d) ? d / 100m : 0m;

    private static string ReadIntPtr(IntPtr ptr, int maxLen)
    {
        if (ptr == IntPtr.Zero) return "";
        var buf = new byte[maxLen];
        Marshal.Copy(ptr, buf, 0, maxLen);
        return Encoding.ASCII.GetString(buf);
    }

    private static void WriteIntPtr(IntPtr ptr, string value, int maxLen)
    {
        if (ptr == IntPtr.Zero) return;
        var bytes = Encoding.ASCII.GetBytes((value + "\0").PadRight(maxLen, ' '));
        int len = Math.Min(bytes.Length, maxLen);
        Marshal.Copy(bytes, 0, ptr, len);
    }

    // métodos de inicialização
    public string VersaoDPOS()
    {
        EnsureLoaded();
        var sb = new StringBuilder(64);
        Fn<Del_VersaoDPOS>("VersaoDPOS")(sb);
        MaybeUnload();
        return sb.ToString().Trim();
    }

    public int InicializaDPOS()
    {
        EnsureLoaded();
        int r = Fn<Del_InicializaDPOS>("InicializaDPOS")();
        MaybeUnload();
        return r;
    }

    public int FinalizaDPOS()
    {
        EnsureLoaded();
        int r = Fn<Del_FinalizaDPOS>("FinalizaDPOS")();
        MaybeUnload();
        return r;
    }

    public int ConfiguraModoDesfazimento(int modo = 1)
    {
        EnsureLoaded();
        int r = Fn<Del_ConfiguraModoDesfazimento>("ConfiguraModoDesfazimento")(modo);
        MaybeUnload();
        return r;
    }

    public int ConfigurarCNPJ(string cnpj)
    {
        EnsureLoaded();
        int r = Fn<Del_ConfiguraCNPJ>("ConfiguraCNPJEstabelecimento")(
            new StringBuilder(cnpj.PadLeft(14, '0')[..14]));
        MaybeUnload();
        return r;
    }

    public int ConfigurarEmpresaLojaPDV(int empresa, int loja, int pdv)
    {
        EnsureLoaded();
        int r = Fn<Del_ConfiguraEmpresaLojaPDV>("ConfiguraEmpresaLojaPDV")(
            new StringBuilder(FmtInt(4, empresa)),
            new StringBuilder(FmtInt(4, loja)),
            new StringBuilder(FmtInt(4, pdv)));
        MaybeUnload();
        return r;
    }

    public int IdentificacaoAC(string nome, string versao)
    {
        EnsureLoaded();
        // Reservado: byte[1]='0' integrado QR='0', cancelamento QR='0'
        var reservado = new StringBuilder(new string(' ', 256));
        reservado[0] = '0'; // byte 1
        reservado[1] = '0'; // byte 2 — QR Code integrado (0=não)
        reservado[2] = '0'; // byte 3 — cancelamento QR (0=não)
        int r = Fn<Del_IdentificacaoAC>("IdentificacaoAutomacaoComercial")(
            new StringBuilder(nome.PadRight(20)[..20]),
            new StringBuilder(versao.PadRight(20)[..20]),
            reservado);
        MaybeUnload();
        return r;
    }


    // transações
    
    public int TransacaoCreditoCompleta(
        decimal valor, int cupom, out int controle,
        string tipoOp = "AV", int parcelas = 1,
        decimal valParcela = 0m, decimal valTaxa = 0m,
        bool permiteAlteracao = false)
    {
        controle = 0;
        EnsureLoaded();

        var reservado = new byte[161];
        Encoding.ASCII.GetBytes("0000".PadRight(161, ' ')).CopyTo(reservado, 0);

        var pControle = new StringBuilder("000000");
        int r = Fn<Del_TransacaoCreditoCompleta>("TransacaoCartaoCreditoCompleta")(
            new StringBuilder(FmtValor(12, valor)),
            new StringBuilder(FmtInt(6, cupom)),
            pControle,
            new StringBuilder(tipoOp.PadRight(2)[..2]),
            new StringBuilder(FmtInt(2, parcelas)),
            new StringBuilder(FmtValor(12, valParcela)),
            new StringBuilder(FmtValor(12, valTaxa)),
            new StringBuilder(permiteAlteracao ? "S" : "N"),
            reservado);

        if (r == 0) controle = ParseControle(pControle);
        MaybeUnload();
        return r;
    }

    public int TransacaoQRCode(decimal valor, int cupom, out int controle, string? transactionParams = null)
    {
        controle = 0;
        EnsureLoaded();
        var pControle = new StringBuilder("000000");
        var pParams = new StringBuilder(transactionParams ?? "");
        int r = Fn<Del_TransacaoQRCode>("TransacaoQRCode")(
            new StringBuilder(FmtValor(12, valor)),
            new StringBuilder(FmtInt(6, cupom)),
            pControle,
            pParams);
        if (r == 0) controle = ParseControle(pControle);
        MaybeUnload();
        return r;
    }

    public int TransacaoDebitoCompleta(
        decimal valor, int cupom, out int controle,
        string tipoOp = "AV", int parcelas = 1, int seqParcela = 0,
        string dataDebito = "00000000",
        decimal valParcela = 0m, decimal valTaxa = 0m,
        bool permiteAlteracao = false)
    {
        controle = 0;
        EnsureLoaded();

        var reservado = new byte[148];
        Encoding.ASCII.GetBytes("0000".PadRight(148, ' ')).CopyTo(reservado, 0);

        var pControle = new StringBuilder("000000");
        int r = Fn<Del_TransacaoDebitoCompleta>("TransacaoCartaoDebitoCompleta")(
            new StringBuilder(FmtValor(12, valor)),
            new StringBuilder(FmtInt(6, cupom)),
            pControle,
            new StringBuilder(tipoOp.PadRight(2)[..2]),
            new StringBuilder(FmtInt(2, parcelas)),
            new StringBuilder(FmtInt(2, seqParcela)),
            new StringBuilder(dataDebito.PadLeft(8, '0')[..8]),
            new StringBuilder(FmtValor(12, valParcela)),
            new StringBuilder(FmtValor(12, valTaxa)),
            new StringBuilder(permiteAlteracao ? "S" : "N"),
            reservado);

        if (r == 0) controle = ParseControle(pControle);
        MaybeUnload();
        return r;
    }

    // Confirmação / desfazimento

    public int ConfirmaCartao(int controle)
    {
        EnsureLoaded();
        int r = Fn<Del_ConfirmaCartao>("ConfirmaCartao")(
            new StringBuilder(FmtInt(6, controle)));
        MaybeUnload();
        return r;
    }

    public int ConfirmaCartaoCredito(int controle)
    {
        EnsureLoaded();
        int r = Fn<Del_ConfirmaCartaoCredito>("ConfirmaCartaoCredito")(
            new StringBuilder(FmtInt(6, controle)));
        MaybeUnload();
        return r;
    }

    public int ConfirmaCartaoDebito(int controle)
    {
        EnsureLoaded();
        int r = Fn<Del_ConfirmaCartaoDebito>("ConfirmaCartaoDebito")(
            new StringBuilder(FmtInt(6, controle)));
        MaybeUnload();
        return r;
    }

    public int DesfazCartao(int controle)
    {
        EnsureLoaded();
        int r = Fn<Del_DesfazCartao>("DesfazCartao")(
            new StringBuilder(FmtInt(6, controle)));
        MaybeUnload();
        return r;
    }

    public int FinalizaTransacao()
    {
        EnsureLoaded();
        int r = Fn<Del_FinalizaTransacao>("FinalizaTransacao")();
        MaybeUnload();
        return r;
    }

    // Diagnóstico
    public string ObtemUltimoErro()
    {
        EnsureLoaded();
        byte[] buffer = new byte[512];
        int result = Fn<Del_ObtemUltimoErro>("ObtemUltimoErro")(buffer);

        // Converte removendo caracteres nulos e espaços
        string erro = Encoding.ASCII.GetString(buffer).Split('\0')[0].Trim();

        return string.IsNullOrEmpty(erro) ? "Nenhum erro detalhado pela DLL." : erro;
    }

    public string ObtemLogTransacaoJson(int? controle = null)
    {
        EnsureLoaded();
        try
        {
            // Se passar o NSU (controle), formata com 6 dígitos. Se não passar, manda StringBuilder vazio (ponteiro nulo/vazio)
            StringBuilder sbControle = controle.HasValue 
                ? new StringBuilder(FmtInt(6, controle.Value)) 
                : new StringBuilder("");

            IntPtr ptrJson = Fn<Del_ObtemLogTransacaoJson>("ObtemLogTransacaoJson")(sbControle);

            if (ptrJson == IntPtr.Zero)
                return string.Empty;

            string jsonResult = Marshal.PtrToStringAnsi(ptrJson) ?? string.Empty;
        
            return jsonResult;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Paykit] Erro ao chamar ObtemLogTransacaoJson: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            MaybeUnload();
        }
}
    public (string completo, string reduzido) ObtemComprovante(int controle)
    {
        EnsureLoaded();
        try
        {
            StringBuilder sbCompleto = new StringBuilder(8192);
            StringBuilder sbReduzido = new StringBuilder(1024);

            string ctrlFormatado = FmtInt(6, controle);

            // Invoca a função nativa da DLL
            Fn<Del_ObtemComprovante>("ObtemComprovanteTransacao")(ctrlFormatado, sbCompleto, sbReduzido);

            // Converte o conteúdo capturado para String limpando espaços em branco nas pontas
            string completo = sbCompleto.ToString().Trim();
            string reduzido = sbReduzido.ToString().Trim();

            return (completo, reduzido);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Paykit] Erro ao obter comprovante da memória: {ex.Message}");
            return (string.Empty, string.Empty);
        }
        finally
        {
            MaybeUnload();
        }
    }

    public string ProcuraPinPad()
    {
        EnsureLoaded();
        var buf = new byte[590];
        Fn<Del_ProcuraPinPad>("ProcuraPinPad")(buf);
        MaybeUnload();
        return Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ');
    }

    private static int ParseControle(StringBuilder sb)
    {
        var s = sb.ToString().Trim('\0', ' ');
        return int.TryParse(s, out int v) ? v : 0;
    }

    public int ConfigurarComunicacao(string config)
    {
        EnsureLoaded();
        int r = Fn<Del_ConfiguraComunicacao>("ConfiguraComunicacaoDTEF")(
            new StringBuilder(config));
        MaybeUnload();
        return r;
    }

    public int BuscarCertificado(string? url, string pathCert)
    {
        EnsureLoaded();
        var pUrl = url != null ? new StringBuilder(url) : new StringBuilder("");
        var pPath = new StringBuilder(pathCert);
        int r = Fn<Del_BuscaCertificado>("BuscaCertificado")(pUrl, pPath);
        MaybeUnload();
        Console.WriteLine($"[Paykit] BuscaCertificado → {r} | path: {pathCert}");
        return r;
    }

    /// Cancela (estorna) uma transação já confirmada e finalizada
    public int CancelarTransacaoAprovada(decimal valor, int cupom, int controle, out int controleCancelamento)
    {
        controleCancelamento = 0;
        EnsureLoaded();

        long valorCentavos = (long)Math.Round(valor * 100);
        var sbValor = new StringBuilder(valorCentavos.ToString().PadLeft(12, '0'), 13);

        var sbCupom = new StringBuilder(cupom.ToString().PadLeft(6, '0'), 7);

        var sbControle = new StringBuilder(controle.ToString().PadLeft(6, '0'), 7);

        var sbPermiteAlteracao = new StringBuilder("N", 2);

        string dataAtual = DateTime.Now.ToString("yyyyMMdd");
        string dadosReservado = $"0{dataAtual}".PadRight(158, ' ');
        var sbReservado = new StringBuilder(dadosReservado, 159);

        Debug.WriteLine($"[PAYKIT] Chamando TransacaoCancelamentoPagamentoCompleta -> Valor: {sbValor}, NSU: {sbControle}, Data: {dataAtual}");

        int resultado = Fn<Del_TransacaoCancelamentoPagamentoCompleta>("TransacaoCancelamentoPagamentoCompleta")(
            sbValor, sbCupom, sbControle, sbPermiteAlteracao, sbReservado
        );

        if (resultado == 0)
        {
            if (int.TryParse(sbControle.ToString(), out int novoCtrl))
            {
                controleCancelamento = novoCtrl;
            }
        }

        MaybeUnload();
        return resultado;
    }

    /// Executa o cancelamento/estorno utilizando a função Completa, mas configurada com permissão de alteração ("S")
    /// para que o Pinpad busque dinamicamente a transação correta com base no cartão inserido
    public int CancelarTransacaoPorCartaoCompleta(decimal valor, int cupom, int controleOriginal, out int controleCancelamento)
    {
        controleCancelamento = 0;
        EnsureLoaded();

        long valorCentavos = (long)Math.Round(valor * 100);
        var sbValor = new StringBuilder(valorCentavos.ToString().PadLeft(12, '0'), 13);

        var sbCupom = new StringBuilder(cupom.ToString().PadLeft(6, '0'), 7);

        var sbControle = new StringBuilder(controleOriginal.ToString().PadLeft(6, '0'), 12);

        var sbPermiteAlteracao = new StringBuilder("S", 2);

        string dataAtual = DateTime.Now.ToString("yyyyMMdd");
        string dadosReservado = $"0{dataAtual}".PadRight(158, ' ');
        var sbReservado = new StringBuilder(dadosReservado, 159);

        Debug.WriteLine($"[PAYKIT] Chamando TransacaoCancelamentoPagamentoCompleta Flexível -> Valor Sugerido: {sbValor}, NSU Sugerido: {sbControle}");

        // Invoca a função robusta mapeada da DLL
        int resultado = Fn<Del_TransacaoCancelamentoPagamentoCompleta>("TransacaoCancelamentoPagamentoCompleta")(
            sbValor, sbCupom, sbControle, sbPermiteAlteracao, sbReservado
        );

        if (resultado == 0)
        {
            if (int.TryParse(sbControle.ToString().Trim(), out int novoCtrl))
            {
                controleCancelamento = novoCtrl;
            }
        }

        MaybeUnload();
        return resultado;
    }

    // IDisposable
    public void Dispose()
    {
        foreach (var h in _handles) if (h.IsAllocated) h.Free();
        _handles.Clear();
        Unload();
    }
}