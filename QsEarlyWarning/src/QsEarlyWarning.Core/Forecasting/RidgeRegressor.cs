namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Closed-form ridge regression, hand-rolled (no ML dependency). Features are standardized on the
/// training rows only (mean/σ stored and reapplied at predict); the intercept is UNPENALIZED. NaN
/// feature values are imputed to the training column mean (→ 0 after standardization). Solved via
/// Cholesky of the ridge-regularized normal matrix `ZᵀZ + λ·diag(0,1,…,1)` (SPD for λ>0 on the
/// standardized feature block); on a non-SPD/rank-deficient pivot the caller retries with a higher λ
/// (ridge floor) and, failing that, falls back to a baseline for that horizon.
/// </summary>
public sealed class RidgeRegressor
{
    private double[] _mean = Array.Empty<double>();
    private double[] _std = Array.Empty<double>();
    private double[] _beta = Array.Empty<double>();   // length p+1, index 0 = intercept
    public bool Fitted { get; private set; }

    public sealed class NotFittableException(string m) : Exception(m);

    public void Fit(double[][] x, double[] y, double lambda)
    {
        int n = x.Length; if (n == 0) throw new NotFittableException("no rows");
        int p = x[0].Length;
        _mean = new double[p]; _std = new double[p];
        // column mean over finite values; σ over mean-imputed values
        for (int j = 0; j < p; j++)
        {
            double s = 0; int c = 0;
            for (int i = 0; i < n; i++) if (double.IsFinite(x[i][j])) { s += x[i][j]; c++; }
            _mean[j] = c > 0 ? s / c : 0;
            double v = 0;
            for (int i = 0; i < n; i++) { double xi = double.IsFinite(x[i][j]) ? x[i][j] : _mean[j]; v += (xi - _mean[j]) * (xi - _mean[j]); }
            _std[j] = Math.Sqrt(v / Math.Max(1, n - 1));
            if (_std[j] < 1e-9) _std[j] = 1;   // constant column → no scaling, penalty makes its weight ~0
        }

        int d = p + 1;                          // + intercept
        var z = new double[n][];
        for (int i = 0; i < n; i++)
        {
            var row = new double[d]; row[0] = 1.0;
            for (int j = 0; j < p; j++) row[j + 1] = (Impute(x[i][j], _mean[j]) - _mean[j]) / _std[j];
            z[i] = row;
        }

        // A = ZᵀZ + λ·diag(0,1,…,1) ; b = Zᵀy
        var a = new double[d][]; for (int r = 0; r < d; r++) a[r] = new double[d];
        var b = new double[d];
        for (int i = 0; i < n; i++)
        {
            var zi = z[i];
            for (int r = 0; r < d; r++)
            {
                b[r] += zi[r] * y[i];
                for (int cc = r; cc < d; cc++) a[r][cc] += zi[r] * zi[cc];
            }
        }
        for (int r = 0; r < d; r++) for (int cc = r + 1; cc < d; cc++) a[cc][r] = a[r][cc]; // symmetric
        for (int r = 1; r < d; r++) a[r][r] += lambda; // penalize features, not intercept

        _beta = CholeskySolve(a, b);   // throws NotFittableException if not SPD
        Fitted = true;
    }

    public double Predict(double[] x)
    {
        if (!Fitted) throw new NotFittableException("not fitted");
        double s = _beta[0];
        for (int j = 0; j < x.Length; j++) s += _beta[j + 1] * ((Impute(x[j], _mean[j]) - _mean[j]) / _std[j]);
        return s;
    }

    private static double Impute(double v, double mean) => double.IsFinite(v) ? v : mean;

    private static double[] CholeskySolve(double[][] a, double[] b)
    {
        int d = b.Length;
        var l = new double[d][]; for (int i = 0; i < d; i++) l[i] = new double[d];
        for (int i = 0; i < d; i++)
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i][j];
                for (int k = 0; k < j; k++) sum -= l[i][k] * l[j][k];
                if (i == j)
                {
                    if (sum <= 1e-12) throw new NotFittableException("normal matrix not positive-definite");
                    l[i][j] = Math.Sqrt(sum);
                }
                else l[i][j] = sum / l[j][j];
            }
        // forward solve L y = b, then Lᵀ β = y
        var yv = new double[d];
        for (int i = 0; i < d; i++) { double s = b[i]; for (int k = 0; k < i; k++) s -= l[i][k] * yv[k]; yv[i] = s / l[i][i]; }
        var beta = new double[d];
        for (int i = d - 1; i >= 0; i--) { double s = yv[i]; for (int k = i + 1; k < d; k++) s -= l[k][i] * beta[k]; beta[i] = s / l[i][i]; }
        return beta;
    }
}
