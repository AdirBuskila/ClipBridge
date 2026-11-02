package com.clipbridge;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileNotFoundException;

/**
 * Lets the app you paste into read the screenshot. Not exported: Android grants
 * read access to a single image at paste time.
 */
public class ClipProvider extends ContentProvider {
    static final String AUTHORITY = "com.clipbridge.clips";

    static File clipDir(Context context) {
        File dir = new File(context.getCacheDir(), "clips");
        dir.mkdirs();
        return dir;
    }

    static Uri uriFor(File file) {
        return new Uri.Builder().scheme("content").authority(AUTHORITY).appendPath(file.getName()).build();
    }

    private File resolve(Uri uri) throws FileNotFoundException {
        String name = uri.getLastPathSegment();
        if (name == null || !name.matches("[0-9]+\\.png")) throw new FileNotFoundException(String.valueOf(uri));
        File file = new File(clipDir(getContext()), name);
        if (!file.isFile()) throw new FileNotFoundException(String.valueOf(uri));
        return file;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        return ParcelFileDescriptor.open(resolve(uri), ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public String getType(Uri uri) {
        return "image/png";
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        File file;
        try {
            file = resolve(uri);
        } catch (FileNotFoundException e) {
            return null;
        }
        String[] cols = projection != null ? projection : new String[] {OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE};
        Object[] row = new Object[cols.length];
        for (int i = 0; i < cols.length; i++) {
            if (OpenableColumns.DISPLAY_NAME.equals(cols[i])) row[i] = "screenshot-" + file.getName();
            else if (OpenableColumns.SIZE.equals(cols[i])) row[i] = file.length();
        }
        MatrixCursor cursor = new MatrixCursor(cols, 1);
        cursor.addRow(row);
        return cursor;
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        throw new UnsupportedOperationException();
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        throw new UnsupportedOperationException();
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        throw new UnsupportedOperationException();
    }
}
