using System.Runtime.InteropServices;

namespace Acusa.Sdk.Integration.Contpaqi.Native;

/// <summary>
/// Estructuras del SDK de CONTPAQi Comercial tal como las documenta el manual del SDK (alineación a 4 bytes,
/// cadenas ANSI de longitud fija con el terminador incluido). Los nombres de los campos son los del SDK.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
internal struct TProducto
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string cCodigoProducto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 61)] public string cNombreProducto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string cDescripcionProducto;
    public int cTipoProducto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)] public string cFechaAltaProducto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)] public string cFechaBaja;
    public int cStatusProducto;
    public int cControlExistencia;
    public int cMetodoCosteo;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string cCodigoUnidadBase;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string cCodigoUnidadNoConvertible;
    public double cPrecio1;
    public double cPrecio2;
    public double cPrecio3;
    public double cPrecio4;
    public double cPrecio5;
    public double cPrecio6;
    public double cPrecio7;
    public double cPrecio8;
    public double cPrecio9;
    public double cPrecio10;
    public double cImpuesto1;
    public double cImpuesto2;
    public double cImpuesto3;
    public double cRetencion1;
    public double cRetencion2;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 61)] public string cNombreCaracteristica1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 61)] public string cNombreCaracteristica2;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 61)] public string cNombreCaracteristica3;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)] public string cCodigoValorClasificacion1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)] public string cCodigoValorClasificacion2;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)] public string cCodigoValorClasificacion3;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)] public string cCodigoValorClasificacion4;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)] public string cCodigoValorClasificacion5;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)] public string cCodigoValorClasificacion6;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 51)] public string cTextoExtra1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 51)] public string cTextoExtra2;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 51)] public string cTextoExtra3;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)] public string cFechaExtra;
    public double cImporteExtra1;
    public double cImporteExtra2;
    public double cImporteExtra3;
    public double cImporteExtra4;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
internal struct TDocumento
{
    public double aFolio;
    public int aNumMoneda;
    public double aTipoCambio;
    public double aImporte;
    public double aDescuentoDoc1;
    public double aDescuentoDoc2;
    public int aSistemaOrigen;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodConcepto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)] public string aSerie;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)] public string aFecha;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodigoCteProv;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodigoAgente;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string aReferencia;
    public int aAfecta;
    public double aGasto1;
    public double aGasto2;
    public double aGasto3;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
internal struct TMovimiento
{
    public int aConsecutivo;
    public double aUnidades;
    public double aPrecio;
    public double aCosto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodProdSer;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodAlmacen;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string aReferencia;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodClasificacion;
}
